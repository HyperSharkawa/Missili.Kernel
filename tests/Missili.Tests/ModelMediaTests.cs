using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Tools.ModelMock;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>实际媒体、模型插件和协议 Mock 验证转述缓存及共享工作。</summary>
public sealed class ModelMediaTests
{
    /// <summary>各协议按真实 JSON 体积组批，完整成功后才缓存整图描述。</summary>
    [Theory]
    [InlineData(ModelProtocol.ChatCompletions)]
    [InlineData(ModelProtocol.Responses)]
    [InlineData(ModelProtocol.Anthropic)]
    public async Task ImageBatchesMeasureActualWireBytesAndCache(ModelProtocol protocol)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario(), "http://127.0.0.1:0", token);
        string variable = "MISSILI_MEDIA_TEST_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ModelConfiguration config = ImageConfiguration(mock.BaseAddress, variable, protocol);
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
            await LoadProviderAsync(fixture, protocol, token);
            using Image<Rgba32> image = new(8, 100, new Rgba32(100, 40, 200));
            Guid mediaId = await ImagePreparationTests.ImportAsync(fixture, image, "PNG", token);
            ConversationProfileSnapshot profile = fixture.Runtime.Models.Profiles.Resolve(TestStore.Address());
            MediaTranscriptionResult result = await fixture.Runtime.Models.Transcriptions.EnsureAsync(mediaId, profile, cancellationToken: token);
            Assert.True(result.Succeeded, result.Error);
            Assert.True(mock.TotalRequests > 1);
            Assert.All(mock.Requests, request => Assert.InRange(request.BodyBytes, 1, 4000));
            Assert.Equal(mock.TotalRequests, result.Cached!.Sources.Length);
            for (int index = 0; index < result.Cached.Sources.Length; index++)
            {
                ModelCallRecord call = (await fixture.Runtime.Models.Calls.GetCallAsync(result.Cached.Sources[index].CallId!.Value, token))!;
                await using ModelDetailRead detail = (await fixture.Runtime.Models.Calls.OpenDetailAsync(call.Attempts[0].Id, token))!;
                using ZipArchive archive = new(detail.Content, ZipArchiveMode.Read, leaveOpen: true);
                await using Stream input = archive.GetEntry("input.json")!.Open();
                ModelAttemptRequest attempt = (await JsonSerializer.DeserializeAsync<ModelAttemptRequest>(input, cancellationToken: token))!;
                using IModelProviderLease lease = fixture.Runtime.ModelProviders.TryAcquire(protocol)!;
                Assert.Equal(mock.Requests[index].BodyBytes, await lease.MeasureAsync(attempt, token));
                using StreamReader requestJson = new(archive.GetEntry("request.json")!.Open());
                string stored = await requestJson.ReadToEndAsync(token);
                Assert.DoesNotContain("base64,", stored); Assert.Contains("media:", stored);
            }
            int requests = mock.TotalRequests;
            MediaTranscriptionResult reused = await fixture.Runtime.Models.Transcriptions.EnsureAsync(mediaId,
                profile with { Profile = profile.Profile with { ImagePrompt = "新提示不自动使缓存失效" } }, cancellationToken: token);
            Assert.Equal(result.Cached.Text, reused.Cached!.Text); Assert.Equal(requests, mock.TotalRequests);
            await mock.DisposeAsync();
            MediaTranscriptionResult failedRefresh = await fixture.Runtime.Models.Transcriptions.EnsureAsync(mediaId, profile, refresh: true, cancellationToken: token);
            Assert.False(failedRefresh.Succeeded); Assert.Equal(result.Cached.Text, failedRefresh.Cached!.Text);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>一个等待者取消不会取消另一个等待者共用的真实模型请求。</summary>
    [Fact]
    public async Task CancellingOneWaiterKeepsSharedTranscriptionRunning()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { WaitForSignal = "reply" } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_MEDIA_TEST_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: ImageConfiguration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions));
            await LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            using Image<Rgba32> image = new(8, 8, new Rgba32(100, 40, 200));
            Guid firstId = await ImagePreparationTests.ImportAsync(fixture, image, "PNG", token);
            Guid secondId = await ImagePreparationTests.ImportAsync(fixture, image, "PNG", token);
            ConversationProfileSnapshot profile = fixture.Runtime.Models.Profiles.Resolve(TestStore.Address());
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<MediaTranscriptionResult> first = fixture.Runtime.Models.Transcriptions.EnsureAsync(firstId, profile, cancellationToken: cancellation.Token);
            await mock.NextRequestAsync(token);
            Task<MediaTranscriptionResult> second = fixture.Runtime.Models.Transcriptions.EnsureAsync(secondId, profile, cancellationToken: token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            mock.Release("reply");
            Assert.True((await second).Succeeded);
            Assert.Equal(1, mock.TotalRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>自定义兼容服务按明确配置使用流式模式，不按名称推断转写能力。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioUsesRealMultipartSdkAndCaches(bool streaming)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { Text = "合成语音转写" } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_MEDIA_TEST_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ModelConfiguration config = ModelConfigurationTests.Configuration(new Uri(mock.BaseAddress, "/v1"), variable);
            config = config with
            {
                Providers = config.Providers.Add(new ModelProviderDefinition { Id = "speech", Protocol = ModelProtocol.AudioTranscription, BaseUrl = new Uri(mock.BaseAddress, "/v1"), ApiKeyEnvironmentVariable = variable }),
                Models = config.Models.Add(new ModelDefinition { Id = "speech", Name = "whisper-1", ProviderId = "speech", Streaming = streaming }),
                Profiles = [config.Profiles[0] with { AudioTranscriptionEnabled = true, TranscriptionModels = ["speech"], Execution = new ModelExecutionOptions { Rounds = 1 } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config);
            await LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            using MemoryStream audio = Wav();
            MediaStatus imported = await fixture.Runtime.MediaImporter.ImportAsync(MediaKind.Audio, audio, cancellationToken: token);
            MediaTranscriptionResult result = await fixture.Runtime.Models.Transcriptions.EnsureAsync(imported.Id, fixture.Runtime.Models.Profiles.Resolve(TestStore.Address()), cancellationToken: token);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("合成语音转写", result.Cached!.Text);
            ModelMockRequest request = Assert.Single(mock.Requests);
            Assert.Equal(streaming, request.Streaming); Assert.Equal("audio.wav", request.AudioFileName); Assert.Equal("audio/wav", request.AudioMimeType); Assert.Equal(46, request.AudioBytes);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>创建只使用合成端点的图片候选配置，预算足够单片但不足所有切片。</summary>
    internal static ModelConfiguration ImageConfiguration(Uri address, string variable, ModelProtocol protocol)
    {
        ModelConfiguration config = ModelConfigurationTests.Configuration(protocol == ModelProtocol.Anthropic ? address : new Uri(address, "/v1"), variable);
        return config with
        {
            Providers = [config.Providers[0] with { Protocol = protocol }],
            Models = [new ModelDefinition { Id = "vision", Name = "synthetic-vision", ProviderId = "test", Images = true, MaxRequestBytes = 4000 }],
            Profiles = [config.Profiles[0] with { ReplyModels = ["vision"], ImageModels = ["vision"], ImageTranscriptionEnabled = true,
                ImagePreparation = new ImagePreparationOptions { MaxWidth = 16, MaxHeight = 16, Overlap = 2, MaxRequestBytes = 4000 },
                Execution = new ModelExecutionOptions { Rounds = 1, CaptureSuccessfulDetails = true } }]
        };
    }

    /// <summary>动态加载实际插件，测试工程不引用待卸载 SDK 类型。</summary>
    internal static async Task LoadProviderAsync(RuntimeFixture fixture, ModelProtocol protocol, CancellationToken token)
    {
        string package = protocol == ModelProtocol.Anthropic ? "anthropic" : "openai";
        PluginStatus loaded = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Providers", package), RuntimeFixture.Configuration(), token);
        Assert.Equal(PluginState.Active, loaded.State);
    }

    /// <summary>构造标准单采样 WAV，避免外部音频文件或解码器依赖。</summary>
    internal static MemoryStream Wav()
    {
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(38); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(2); writer.Write((short)0);
        }
        stream.Position = 0; return stream;
    }
}
