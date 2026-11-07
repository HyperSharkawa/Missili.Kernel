using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Missili.Abstractions;
using Missili.Model.Mock;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>媒体归档完成、配置更新和适配器格式准备均使用真实宿主服务。</summary>
public sealed class ModelArchiveIntegrationTests
{
    /// <summary>配置切换到混合策略后扫描已有原件；纯文本输入在单网络槽下仍能完成转述与回复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchivedImagesConnectToBackgroundAndContext(bool background)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using Image<Rgba32> image = new(8, 8, new Rgba32(20, 60, 120));
        using MemoryStream png = new(); await image.SaveAsPngAsync(png, token); byte[] bytes = png.ToArray();
        await using TestHttpServer media = await TestHttpServer.StartAsync(context => context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask(), token);
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario { Default = new ModelMockStep { Text = "实际图片转述" } }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_ARCHIVE_MODEL_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration config = ModelMediaTests.ImageConfiguration(mock.BaseAddress, variable, ModelProtocol.ChatCompletions);
            config = config with { Providers = [config.Providers[0] with { MaxConcurrency = 1 }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: config, modelOptions: new ModelOptions { NetworkConcurrency = 1, QueueCapacity = 1 });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            fixture.Runtime.Models.StartBackgroundScan();
            MessageSnapshot message = (await fixture.Runtime.ReceiveAsync(TestStore.Message() with
            {
                Segments =
                [new IncomingSegment { Kind = SegmentTypes.Image, Media = [new IncomingMedia { Kind = MediaKind.Image, Source = MediaSource.Http(media.BaseAddress) }] }]
            }, token)).Message!;
            Guid id = message.Segments[0].MediaIds[0];
            await using (MediaReadResult read = await fixture.Runtime.Media.OpenReadAsync(id, token)) Assert.Equal(MediaState.Available, read.Status.State);
            Assert.Equal(0, mock.TotalRequests);
            config = config with { Profiles = [config.Profiles[0] with { Images = background ? ModelMediaPolicy.TranscriptionPreferred : ModelMediaPolicy.TextOnly }] };
            await fixture.WriteModelsAsync(config, token);
            await fixture.Runtime.Profiles.ReloadAsync(token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (background)
            {
                await mock.NextRequestAsync(deadline.Token);
                TranscriptionSnapshot? cached;
                do { cached = await fixture.Runtime.Models.Transcriptions.GetAsync(id, deadline.Token); if (cached?.Text is null) await Task.Delay(1, deadline.Token); } while (cached?.Text is null);
                Assert.Equal("实际图片转述", cached.Text!.Split('\n')[^1]);
                Assert.Equal(1, mock.TotalRequests);
            }
            else
            {
                ConversationContextSnapshot context = await fixture.Runtime.Models.Contexts.CaptureAsync(new MessageContext { OriginalMessage = message, CurrentContent = message.Segments }, deadline.Token);
                ModelResult result = await fixture.Runtime.Models.GenerateAsync(new ModelRequest { Purpose = "reply", Profile = context.Profile, Input = ModelInput.FromConversation(context, "根据会话资料回答。"), Candidates = ["vision"] }, cancellationToken: deadline.Token);
                Assert.True(result.Complete, result.Error); Assert.Equal(2, mock.TotalRequests);
                Assert.DoesNotContain("image_url", mock.Requests[1].Body!.Value.GetRawText());
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>只有明确路径映射才能取得标准 WAV；成功派生即使适配器随后离线也能复用。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneBotWavRepresentationRequiresMappingAndPreservesOriginal(bool mapping)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using MemoryStream wav = ModelMediaTests.Wav();
            await File.WriteAllBytesAsync(Path.Combine(directory, "converted.wav"), wav.ToArray(), token);
            await using TestHttpServer media = await TestHttpServer.StartAsync(context => context.Response.Body.WriteAsync("#!SILK_V3synthetic"u8.ToArray(), context.RequestAborted).AsTask(), token);
            await using OneBotFixture fixture = await OneBotFixture.CreateAsync(false, false, token, settings =>
            { if (mapping) settings["pathMappings"] = new[] { new Dictionary<string, string> { ["remoteRoot"] = "/remote", ["localRoot"] = directory } }; });
            fixture.Peer.ResponseHandler = async (action, cancellation) =>
            {
                if (action.Action != "get_record") return false;
                Assert.Equal("wav", action.Parameters.GetProperty("out_format").GetString());
                await action.RespondAsync("ok", 0, JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["file"] = "/remote/converted.wav" }), cancellation); return true;
            };
            JsonElement frame = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["self_id"] = 10001,
                ["post_type"] = "message",
                ["message_type"] = "group",
                ["sub_type"] = "normal",
                ["time"] = 1,
                ["message_id"] = 801,
                ["group_id"] = 20001,
                ["user_id"] = 30001,
                ["sender"] = new Dictionary<string, object> { ["user_id"] = 30001 },
                ["message"] = new[] { new Dictionary<string, object> { ["type"] = "record", ["data"] = new Dictionary<string, string> { ["file"] = "original.silk", ["url"] = media.BaseAddress.AbsoluteUri } } }
            });
            await fixture.Peer.SendAsync(fixture.Role, frame.GetRawText(), token: token);
            MessageSnapshot message = await fixture.WaitMessageAsync("801", token);
            Guid id = message.Segments[0].MediaIds[0];
            await using MediaReadResult original = await fixture.Runtime.Media.OpenReadAsync(id, token);
            ModelDefinition model = new() { Id = "speech", ProviderId = "speech", Name = "synthetic", AudioFormats = ["wav"] };
            if (!mapping)
            {
                await Assert.ThrowsAsync<MediaAcquisitionException>(() => fixture.Runtime.Models.Representations.PrepareAudioAsync(id, model, token));
                return;
            }
            MediaRepresentation result = await fixture.Runtime.Models.Representations.PrepareAudioAsync(id, model, token);
            Assert.NotEqual(id, result.MediaId); Assert.Equal(original.Status.ContentKey, result.OriginalContentKey);
            MediaStatus derived = (await fixture.Runtime.Media.GetStatusAsync(result.MediaId, token))!;
            Assert.False(derived.Representation!.Value.GetProperty("original").GetBoolean());
            Assert.Equal("wav", derived.Representation.Value.GetProperty("format").GetString());
            await fixture.Runtime.Plugins.StopAsync("missili.onebot11", token);
            Assert.Equal(result.MediaId, (await fixture.Runtime.Models.Representations.PrepareAudioAsync(id, model, token)).MediaId);
            Assert.Single(fixture.Peer.RecordedActions, action => action.GetProperty("action").GetString() == "get_record");
        }
        finally { Directory.Delete(directory, true); }
    }
}
