using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.ServerSentEvents;
using Missili.Abstractions;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Missili.Providers.OpenAI;

// 所选正式 SDK 仍将 Responses 和流式转写标为实验接口；仅在此驱动显式采用。
// https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0
#pragma warning disable OPENAI001
#pragma warning disable SCME0005

/// <summary>官方 OpenAI SDK 驱动，三个协议入口随同一插件代次发布。</summary>
public sealed class OpenAIPlugin : IPlugin, IModelProtocolDriver
{
    private IMediaStore? _media;
    private TimeProvider _clock = TimeProvider.System;
    /// <summary>只登记协议，初始化不连接外部模型服务。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _media = context.Media; _clock = context.Clock;
        foreach (ModelProtocol protocol in new[] { ModelProtocol.ChatCompletions, ModelProtocol.Responses, ModelProtocol.AudioTranscription })
            context.ModelProviders.Register(new ModelProviderRegistration { Protocol = protocol, Driver = this });
        return Task.CompletedTask;
    }
    /// <summary>在途调用由 Nexus 等待，客户端在各次调用结束时释放。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>使用 SDK 请求类型的正式序列化计算完整 JSON 大小，包含图片 Base64 和参数。</summary>
    public async Task<long> MeasureAsync(ModelAttemptRequest request, CancellationToken cancellationToken)
    {
        if (request.Provider.Protocol == ModelProtocol.AudioTranscription) throw new ModelInputException("音频转写使用文件上限，不属于 JSON 图片组批。");
        JsonObject body = await new ProtocolPayload(_media!).BuildAsync(request, cancellationToken).ConfigureAwait(false);
        BinaryData input = BinaryData.FromString(body.ToJsonString());
        return request.Provider.Protocol == ModelProtocol.ChatCompletions
            ? ModelReaderWriter.Write(ModelReaderWriter.Read<ChatCompletionOptions>(input)!).ToMemory().Length
            : ModelReaderWriter.Write(ModelReaderWriter.Read<CreateResponseOptions>(input)!).ToMemory().Length;
    }

    /// <summary>执行一次真实 SDK 调用，关闭内部重试和总超时。
    /// 依据：https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0 。</summary>
    public async Task<ModelProtocolResult> ExecuteAsync(ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken cancellationToken)
    {
        ProtocolAccumulator accumulator = new(request.Provider.Protocol);
        try
        {
            string credential = Environment.GetEnvironmentVariable(request.Provider.ApiKeyEnvironmentVariable)
                ?? throw new ModelInputException("未设置指定的模型凭据环境变量。");
            ProtocolPayload payload = new(_media!);
            JsonObject? body = request.Provider.Protocol == ModelProtocol.AudioTranscription ? null : await payload.BuildAsync(request, cancellationToken).ConfigureAwait(false);
            using HttpClient http = new(new ModelHttpTransport(request, observer, _clock, payload.MediaValues)) { Timeout = Timeout.InfiniteTimeSpan };
            OpenAIClientOptions options = new()
            {
                Endpoint = request.Provider.BaseUrl,
                Transport = new HttpClientPipelineTransport(http),
                RetryPolicy = new ClientRetryPolicy(0),
                NetworkTimeout = Timeout.InfiniteTimeSpan
            };
            if (request.Provider.Protocol == ModelProtocol.AudioTranscription)
                return await TranscribeAsync(new AudioClient(request.Model.Name, new ApiKeyCredential(credential), options), request, observer, cancellationToken).ConfigureAwait(false);
            if (request.Provider.Protocol == ModelProtocol.ChatCompletions)
            {
                ChatClient client = new(request.Model.Name, new ApiKeyCredential(credential), options);
                ChatMessage[] messages = body!["messages"]!.AsArray().Select(node => ModelReaderWriter.Read<ChatMessage>(BinaryData.FromString(node!.ToJsonString()))!).ToArray();
                body.Remove("messages");
                ChatCompletionOptions settings = ModelReaderWriter.Read<ChatCompletionOptions>(BinaryData.FromString(body.ToJsonString()))!;
                if (request.Request.Stream)
                {
                    await foreach (StreamingChatCompletionUpdate update in client.CompleteChatStreamingAsync(messages, settings, cancellationToken).ConfigureAwait(false))
                        await UpdateAsync(accumulator, ModelReaderWriter.Write(update), observer, cancellationToken).ConfigureAwait(false);
                    accumulator.EndStream();
                }
                else
                {
                    ClientResult<ChatCompletion> result = await client.CompleteChatAsync(messages, settings, cancellationToken).ConfigureAwait(false);
                    using JsonDocument response = JsonDocument.Parse(result.GetRawResponse().Content);
                    accumulator.Complete(response.RootElement);
                }
            }
            else
            {
                ResponsesClient client = new(new ApiKeyCredential(credential), new ResponsesClientOptions
                {
                    Endpoint = request.Provider.BaseUrl,
                    Transport = new HttpClientPipelineTransport(http),
                    RetryPolicy = new ClientRetryPolicy(0),
                    NetworkTimeout = Timeout.InfiniteTimeSpan
                });
                CreateResponseOptions settings = ModelReaderWriter.Read<CreateResponseOptions>(BinaryData.FromString(body!.ToJsonString()))!;
                if (request.Request.Stream)
                {
                    await foreach (StreamingResponseUpdate update in client.CreateResponseStreamingAsync(settings, cancellationToken).ConfigureAwait(false))
                        await UpdateAsync(accumulator, ModelReaderWriter.Write(update), observer, cancellationToken).ConfigureAwait(false);
                    accumulator.EndStream();
                }
                else
                {
                    ClientResult<ResponseResult> result = await client.CreateResponseAsync(settings, cancellationToken).ConfigureAwait(false);
                    using JsonDocument response = JsonDocument.Parse(result.GetRawResponse().Content);
                    accumulator.Complete(response.RootElement);
                }
            }
            return accumulator.Result();
        }
        catch (ModelConsumerException) { throw; }
        catch (ModelRecordingException) { throw; }
        catch (ModelInputException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return accumulator.Result(ModelCallState.Cancelled, "模型调用已取消。", "Cancelled"); }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException or IOException or JsonException or TimeoutException or OperationCanceledException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return accumulator.Result(ModelCallState.Failed, "模型请求失败：" + exception.GetType().Name, exception.GetType().Name); }
    }

    /// <summary>将 SDK 事件转换成共享 JSON 后再发布，宿主不缓存 SDK 类型。</summary>
    private static async Task UpdateAsync(ProtocolAccumulator accumulator, BinaryData data, IModelAttemptObserver observer, CancellationToken token)
    {
        using JsonDocument document = JsonDocument.Parse(data);
        string? text = accumulator.Add(document.RootElement);
        await observer.UpdateAsync(text is null ? "protocol" : "text", text, document.RootElement.Clone(), token).ConfigureAwait(false);
    }

    /// <summary>真实语音转写使用已准备的格式，不通过改扩展名伪造转码。</summary>
    private async Task<ModelProtocolResult> TranscribeAsync(AudioClient client, ModelAttemptRequest request, IModelAttemptObserver observer, CancellationToken token)
    {
        ModelBlock audio = request.Messages.SelectMany(message => message.Content).Single(block => block.Kind == ModelBlockKind.Audio);
        string extension = audio.MimeType switch { "audio/wav" => "wav", "audio/mpeg" => "mp3", "audio/mp4" => "m4a", "audio/flac" => "flac", "audio/ogg" => "ogg", "audio/webm" => "webm", _ => throw new ModelInputException("不支持的语音格式。") };
        if (!request.Model.AudioFormats.Contains(extension)) throw new ModelInputException("候选不支持该语音文件格式。");
        await using MediaReadResult read = await _media!.OpenReadAsync(audio.MediaId!.Value, token).ConfigureAwait(false);
        if (read.Content is null) throw new ModelInputException(read.Error ?? "语音原件不可用。");
        if (read.Status.Length > request.Request.Profile.Profile.MaxAudioBytes) throw new ModelInputException("语音文件超过转写上限。");
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(request.Model.Name), "model");
        form.Add(new StringContent(request.Request.Stream ? "true" : "false"), "stream");
        StreamContent file = new(read.Content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(audio.MimeType!);
        form.Add(file, "file", "audio." + extension);
        Dictionary<string, object> diagnostic = new()
        {
            ["model"] = request.Model.Name,
            ["file"] = "media:" + audio.MediaId,
            ["format"] = extension,
            ["stream"] = request.Request.Stream
        };
        if (request.Model.Parameters is JsonElement parameters)
        {
            foreach (JsonProperty property in parameters.EnumerateObject())
            {
                if (property.Name is "file" or "model" or "stream" or "api_key" or "authorization") throw new ModelInputException("转写扩展不能覆盖框架控制字段。");
                string value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                if (property.Name == "response_format" && value is not ("json" or "verbose_json")) throw new ModelInputException("转写结果须使用 JSON 格式。");
                form.Add(new StringContent(value), property.Name); diagnostic[property.Name] = property.Value.Clone();
            }
        }
        await observer.RequestAsync(JsonSerializer.SerializeToElement(diagnostic), token).ConfigureAwait(false);
        using MultipartRequestContent content = new(form);
        string contentType = form.Headers.ContentType!.ToString();
        RequestOptions options = new() { CancellationToken = token };
        if (!request.Request.Stream)
        {
            ClientResult result = await client.TranscribeAudioAsync(content, contentType, options).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(result.GetRawResponse().Content);
            return new ModelProtocolResult { State = ModelCallState.Succeeded, Complete = true, Content = [ModelBlock.TextBlock(document.RootElement.GetProperty("text").GetString()!)] };
        }
        string text = "";
        bool complete = false;
        try
        {
            // 使用 SDK 的公开原始流入口，由配置声明能力，不让客户端按模型名称改写该决定。
            // https://github.com/openai/openai-dotnet/blob/OpenAI_2.14.0/OpenAI/src/Generated/AudioClient.cs
            await using AsyncStreamingClientResult<SseItem<BinaryData>> stream = await client.TranscribeAudioStreamingAsync(content, contentType, options).ConfigureAwait(false);
            await foreach (SseItem<BinaryData> update in stream.WithCancellation(token).ConfigureAwait(false))
            {
                if (update.Data.ToString() == "[DONE]") break;
                using JsonDocument data = JsonDocument.Parse(update.Data);
                string type = data.RootElement.GetProperty("type").GetString()!;
                if (type == "transcript.text.delta")
                { string delta = data.RootElement.GetProperty("delta").GetString()!; text += delta; await observer.UpdateAsync("text", delta, null, token).ConfigureAwait(false); }
                else if (type == "transcript.text.done") { text = data.RootElement.GetProperty("text").GetString()!; complete = true; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new ModelProtocolResult { State = ModelCallState.Cancelled, Content = [ModelBlock.TextBlock(text) with { Complete = false }], ErrorKind = "Cancelled" }; }
        catch (ModelConsumerException) { throw; }
        catch (ModelRecordingException) { throw; }
        catch (Exception exception) when (exception is ClientResultException or HttpRequestException or IOException or JsonException or TimeoutException)
        { return new ModelProtocolResult { State = ModelCallState.Failed, Content = [ModelBlock.TextBlock(text) with { Complete = false }], Error = "语音转写流失败。", ErrorKind = exception.GetType().Name }; }
        return new ModelProtocolResult { State = complete ? ModelCallState.Succeeded : ModelCallState.Failed, Complete = complete, Content = [ModelBlock.TextBlock(text) with { Complete = complete }], ErrorKind = complete ? null : "Incomplete" };
    }

    /// <summary>流式转交标准 multipart 内容，不把整段音频复制进内存或诊断 JSON。</summary>
    private sealed class MultipartRequestContent : BinaryContent
    {
        private readonly HttpContent _content;
        /// <summary>仅持有当前请求的已准备文件流。</summary>
        public MultipartRequestContent(HttpContent content) => _content = content;
        /// <summary>向 SDK 提供可以确定的 multipart 长度。</summary>
        public override bool TryComputeLength(out long length) { long? size = _content.Headers.ContentLength; length = size ?? 0; return size.HasValue; }
        /// <summary>SDK 异步传输直接读取原件，不额外缓存文件。</summary>
        public override Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default) => _content.CopyToAsync(stream, cancellationToken);
        /// <summary>保留 BinaryContent 的同步契约，生产请求仍使用异步路径。</summary>
        public override void WriteTo(Stream stream, CancellationToken cancellationToken = default) => _content.CopyTo(stream, null, cancellationToken);
        /// <summary>调用范围统一释放 multipart 及其中的独占读取流。</summary>
        public override void Dispose() => _content.Dispose();
    }
}
