using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.ServerSentEvents;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Missili.Abstractions;

namespace Missili.Providers.Anthropic;

/// <summary>Anthropic 官方 SDK 驱动，原生内容块与签名按协议完整保留。</summary>
public sealed class AnthropicPlugin : IPlugin, IModelProtocolDriver
{
    private IMediaStore? _media;
    private TimeProvider _clock = TimeProvider.System;
    /// <summary>暂存协议注册，初始化不发起模型请求。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _media = context.Media; _clock = context.Clock;
        context.ModelProviders.Register(new ModelProviderRegistration { Protocol = ModelProtocol.Anthropic, Driver = this });
        return Task.CompletedTask;
    }
    /// <summary>在途请求随 Nexus 取消并由各次调用释放。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>SDK 原始请求以相同 JSON 元素字典序列化，计量不读取凭据或发起网络请求。</summary>
    public async Task<long> MeasureAsync(ModelAttemptRequest request, CancellationToken cancellationToken)
    {
        JsonObject body = await new ProtocolPayload(_media!).BuildAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body.ToJsonString());
        Dictionary<string, JsonElement> raw = document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        return JsonSerializer.SerializeToUtf8Bytes(raw).LongLength;
    }

    /// <summary>每次请求独占 SDK 客户端，显式凭据、零隐式重试、无总时限。
    /// 依据：https://github.com/anthropics/anthropic-sdk-csharp/tree/Anthropic-v13.4.0 。</summary>
    public async Task<ModelProtocolResult> ExecuteAsync(ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken cancellationToken)
    {
        ProtocolAccumulator accumulator = new(ModelProtocol.Anthropic);
        try
        {
            string credential = Environment.GetEnvironmentVariable(request.Provider.ApiKeyEnvironmentVariable)
                ?? throw new ModelInputException("未设置指定的模型凭据环境变量。");
            ProtocolPayload payload = new(_media!);
            JsonObject body = await payload.BuildAsync(request, cancellationToken).ConfigureAwait(false);
            using HttpClient http = new(new ModelHttpTransport(request, observer, _clock, payload.MediaValues)) { Timeout = Timeout.InfiniteTimeSpan };
            using AnthropicClient client = new(new ClientOptions
            {
                ApiKey = credential,
                BaseUrl = request.Provider.BaseUrl.AbsoluteUri,
                HttpClient = http,
                MaxRetries = 0,
                Timeout = Timeout.InfiniteTimeSpan
            });
            using JsonDocument document = JsonDocument.Parse(body.ToJsonString());
            Dictionary<string, JsonElement> raw = document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
            raw["stream"] = JsonSerializer.SerializeToElement(request.Request.Stream);
            MessageCreateParams parameters = MessageCreateParams.FromRawUnchecked(new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), raw);
            using HttpResponse response = await client.WithRawResponse.Execute(new HttpRequest<MessageCreateParams>
            { Method = HttpMethod.Post, Params = parameters }, cancellationToken).ConfigureAwait(false);
            await using Stream stream = await response.ReadAsStream(cancellationToken).ConfigureAwait(false);
            if (request.Request.Stream)
            {
                // SDK 的强类型响应会进入共享 JSON 反射构造器缓存并保活可回收程序集。
                // 采用 SDK 公开的原始响应入口及 SDK 同样使用的 BCL SSE 解析器，避免该缓存。
                // https://github.com/anthropics/anthropic-sdk-csharp/blob/Anthropic-v13.4.0/src/Anthropic/Core/Sse.cs
                await foreach (SseItem<string> item in SseParser.Create(stream).EnumerateAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (item.EventType == "ping" || item.Data.Length == 0) continue;
                    using JsonDocument update = JsonDocument.Parse(item.Data);
                    JsonElement data = update.RootElement;
                    string? text = accumulator.Add(data);
                    await observer.UpdateAsync(text is null ? "protocol" : "text", text, data.Clone(), cancellationToken).ConfigureAwait(false);
                }
                accumulator.EndStream();
            }
            else
            {
                using JsonDocument result = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                accumulator.Complete(result.RootElement);
            }
            return accumulator.Result();
        }
        catch (ModelConsumerException) { throw; }
        catch (ModelRecordingException) { throw; }
        catch (ModelInputException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return accumulator.Result(ModelCallState.Cancelled, "模型调用已取消。", "Cancelled"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TimeoutException or OperationCanceledException or KeyNotFoundException or InvalidOperationException or FormatException || exception.GetType().Namespace == "Anthropic.Exceptions")
        { return accumulator.Result(ModelCallState.Failed, "模型请求失败：" + exception.GetType().Name, exception.GetType().Name); }
    }
}
