using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Missili.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>框架归档工具通过真实模型循环读取已有内容，刷新使用独立授权和成功缓存。</summary>
public sealed class AgentAssetToolTests
{
    /// <summary>缓存读取零转述请求；刷新许可、失败保留与撤回限制均在实际执行路径生效。</summary>
    [Theory]
    [InlineData("cached", "Succeeded", 0, "已有成功转述")]
    [InlineData("refresh-denied", "Rejected", 0, "已有成功转述")]
    [InlineData("refresh-allowed", "Succeeded", 1, "区域 1 至 1：\n新的成功转述")]
    [InlineData("refresh-failed", "Failed", 1, "已有成功转述")]
    [InlineData("recalled", "Rejected", 0, "已有成功转述")]
    public async Task ArchiveToolsRespectScopeAndRefreshPermission(string mode, string expected, int descriptions, string cachedText)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid mediaId = Guid.Empty, contentId = Guid.Empty;
        JsonElement finalRequest = default;
        int decisions = 0, generated = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.GetProperty("model").GetString() == "B")
            {
                Interlocked.Increment(ref generated);
                if (mode == "refresh-failed") { context.Response.StatusCode = 500; await context.Response.WriteAsync("{\"error\":{\"message\":\"synthetic failure\"}}", context.RequestAborted); }
                else await AgentCommunicationTests.RespondAsync(context, "新的成功转述", []);
                return;
            }
            if (Interlocked.Increment(ref decisions) == 1) await AgentCommunicationTests.RespondAsync(context, null,
            [
                ("read_transcription", new Dictionary<string, object> { ["media_id"] = mediaId, ["refresh"] = mode.StartsWith("refresh", StringComparison.Ordinal) }),
                ("read_content", new Dictionary<string, object> { ["content_id"] = contentId })
            ]);
            else { finalRequest = document.RootElement.Clone(); await AgentCommunicationTests.RespondAsync(context, "合成归档读取完成", []); }
        }, token);
        string variable = "MISSILI_ASSET_TOOL_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(server.BaseAddress, variable, ModelProtocol.ChatCompletions);
            configuration = configuration with
            {
                Models = configuration.Models.Select(model => model with { Tools = true, Images = model.Id == "B" }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Visibility = MessageVisibilityPolicy.Placeholder, ImageModels = ["B"], ImageTranscriptionEnabled = true,
                    Agent = new AgentProfile { Enabled = true, Models = ["A"], Tools = [AgentToolIds.ReadContent, AgentToolIds.ReadTranscription],
                        Permissions = mode is "refresh-allowed" or "refresh-failed" ? [AgentPermissions.RefreshTranscription] : [] } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            using Image<Rgba32> picture = new(2, 2, new Rgba32(50, 80, 120));
            Guid imported = await ImagePreparationTests.ImportAsync(fixture, picture, "PNG", token);
            BatchSendResult pictureSent = await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [new MessageSegment { Kind = SegmentTypes.Image, MediaIds = [imported] }]), token);
            MessageSnapshot pictureMessage = (await fixture.Runtime.History.GetAsync(Assert.Single(pictureSent.Messages).MessageId!.Value, token))!;
            mediaId = pictureMessage.Segments[0].MediaIds[0];
            MediaStatus media = (await fixture.Runtime.Media.GetStatusAsync(mediaId, token))!;
            await fixture.Runtime.Transcriptions.SaveTranscriptionAsync(new TranscriptionSnapshot
            {
                ContentKey = media.ContentKey!,
                Kind = "image.description",
                Text = "已有成功转述",
                CompletedAt = TestStore.Now,
                Sources = [new TranscriptionSource { Provider = "test", Model = "B", Prompt = "private-transcription-prompt" }]
            }, 0, token);
            MessageSnapshot quoted = (await fixture.Runtime.ReceiveAsync(TestStore.Message("引用原文") with { PlatformMessageId = "quoted-source" }, token)).Message!;
            contentId = await fixture.Runtime.Contents.RequestContentAsync(new ContentSource { Address = TestStore.Address(), Kind = "reply", PlatformId = "quoted-source" }, token);
            IInfrastructureStore store = (IInfrastructureStore)fixture.Runtime.History;
            await store.SaveContentAsync(contentId, new ContentExpansion
            {
                State = ContentState.Partial,
                Nodes = [new IncomingContentNode { Path = "0", Segments = [new IncomingSegment { Kind = SegmentTypes.Text, Text = "引用原文" }] }],
                Issues = [new ContentIssue { Path = "0/child", Code = "synthetic", Description = "合成的局部失败说明" }],
                Checkpoint = JsonSerializer.SerializeToElement("private-archive-checkpoint")
            }, TestStore.Now, token);
            IConversationAssetAccess access = (IConversationAssetAccess)fixture.Runtime.History;
            Assert.False(await access.CanReadAsync(TestStore.Address("other"), ConversationAssetKind.Media, mediaId, MessageVisibilityPolicy.Original, token));
            Assert.False(await access.CanReadAsync(TestStore.Address("other"), ConversationAssetKind.Content, contentId, MessageVisibilityPolicy.Original, token));
            if (mode == "recalled")
            {
                await store.SetVisibilityAsync(pictureMessage.Id, true, false, token);
                await store.SetVisibilityAsync(quoted.Id, true, false, token);
            }
            fixture.Runtime.OpenAgents();
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message("显式读取已有归档"), token)).Message!;
            AgentWakeResult admission = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = source.Id, CurrentContent = source.Segments }, token);
            Assert.True(admission.Registered, admission.Error);
            await FinishAsync(fixture, admission.TaskId!.Value, token);
            JsonElement[] results = finalRequest.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "tool").Select(Result).ToArray();
            Assert.Equal(expected, results[0].GetProperty("state").GetString());
            Assert.Equal(mode == "recalled" ? "Rejected" : "Succeeded", results[1].GetProperty("state").GetString());
            Assert.DoesNotContain("private-transcription-prompt", string.Concat(results.Select(result => result.GetRawText())));
            Assert.DoesNotContain("private-archive-checkpoint", string.Concat(results.Select(result => result.GetRawText())));
            if (mode != "recalled")
            {
                using JsonDocument content = JsonDocument.Parse(results[1].GetProperty("data").GetProperty("content").GetString()!);
                Assert.Equal((int)ContentState.Partial, content.RootElement.GetProperty("State").GetInt32());
                Assert.Equal("合成的局部失败说明", content.RootElement.GetProperty("Issues")[0].GetProperty("Description").GetString());
            }
            Assert.Equal(descriptions, generated); Assert.Equal(2, decisions);
            Assert.Equal(cachedText, (await fixture.Runtime.Models.Transcriptions.GetAsync(mediaId, token))!.Text);
            AgentTaskSnapshot task = (await ((IAgentHistory)store).GetTaskAsync(TestStore.Address(), admission.TaskId.Value, token))!;
            Assert.Equal(2, task.Usage.ModelCalls);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>读取实际 SDK 发送的工具结果，不通过生产编码器生成预期。</summary>
    private static JsonElement Result(JsonElement message)
    {
        JsonElement content = message.GetProperty("content");
        string text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
        using JsonDocument document = JsonDocument.Parse(text); return document.RootElement.Clone();
    }

    /// <summary>只有真实释放归属才表示任务完成，不用单次模型返回替代收尾。</summary>
    private static async Task FinishAsync(RuntimeFixture fixture, Guid task, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            AgentTaskSnapshot state = (await ((IAgentHistory)fixture.Runtime.History).GetTaskAsync(TestStore.Address(), task, deadline.Token))!;
            if (state.ReleasedAt.HasValue) { Assert.Equal(AgentTaskState.Completed, state.State); return; }
            await Task.Delay(10, deadline.Token);
        }
    }
}
