using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Missili.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>真实工具循环中观察上下文更新，不以单独窗口查询替代 Agent 续接验收。</summary>
public sealed class AgentContextTests
{
    /// <summary>规范身份合并不重复聊天，转述修订会更新原位置的媒体投影。</summary>
    [Fact]
    public async Task CanonicalMergeAndLateTranscriptionUpdateExistingContext()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Channel<JsonElement> requests = Channel.CreateBounded<JsonElement>(2);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            int index = Interlocked.Increment(ref calls);
            await requests.Writer.WriteAsync(document.RootElement.Clone(), context.RequestAborted);
            if (index == 1) await release.Task.WaitAsync(context.RequestAborted);
            await AgentCommunicationTests.RespondAsync(context, null, [(index == 1 ? "list_agents" : "finish_turn", new())]);
        }, token);
        string variable = "MISSILI_CONTEXT_ALIAS_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(server.BaseAddress, variable, ModelProtocol.ChatCompletions);
            configuration = configuration with
            {
                Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { Images = ModelMediaPolicy.TextOnly,
                    Agent = new AgentProfile { Enabled = true, Models = ["A"], Tools = [AgentToolIds.List, AgentToolIds.FinishTurn] } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            IInfrastructureStore store = (IInfrastructureStore)fixture.Runtime.History;
            MessageSnapshot intent = await store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("别名内容")] }, TestStore.Now, Guid.NewGuid(), token);
            await store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Confirmed }, token);
            using Image<Rgba32> picture = new(2, 2, new Rgba32(30, 60, 90));
            Guid imported = await ImagePreparationTests.ImportAsync(fixture, picture, "PNG", token);
            BatchSendResult sent = await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [new MessageSegment { Kind = SegmentTypes.Image, MediaIds = [imported] }]), token);
            MessageSnapshot imageMessage = (await fixture.Runtime.History.GetAsync(sent.Messages[0].MessageId!.Value, token))!;
            Guid mediaId = imageMessage.Segments[0].MediaIds[0];
            MediaStatus media = (await fixture.Runtime.Media.GetStatusAsync(mediaId, token))!;
            TranscriptionSnapshot old = new() { ContentKey = media.ContentKey!, Kind = "image.description", Text = "旧媒体说明", CompletedAt = TestStore.Now };
            await fixture.Runtime.Transcriptions.SaveTranscriptionAsync(old, 0, token);
            MessageSnapshot source = (await fixture.Runtime.ReceiveAsync(TestStore.Message("观察内容变化"), token)).Message!;
            AgentWakeResult admission = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = source.Id, CurrentContent = source.Segments }, token);
            JsonElement first = await requests.Reader.ReadAsync(token);
            Assert.Equal(1, Count(Text(first), "别名内容")); Assert.Contains("旧媒体说明", Text(first));
            MessageSnapshot observation = (await fixture.Runtime.ReceiveAsync(TestStore.Message("别名内容") with
            { Direction = MessageDirection.Outgoing, SenderId = TestStore.Address().BotAccountId, PlatformMessageId = "observed-own" }, token)).Message!;
            await store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "observed-own" }, token);
            Assert.Equal(observation.Id, (await fixture.Runtime.History.GetAsync(intent.Id, token))!.Id);
            long revision = (await fixture.Runtime.Models.Transcriptions.GetAsync(mediaId, token))!.Revision;
            await fixture.Runtime.Transcriptions.SaveTranscriptionAsync(old with { Text = "后来完成的媒体说明" }, revision, token);
            release.TrySetResult();
            JsonElement updated = await requests.Reader.ReadAsync(token);
            string text = Text(updated);
            Assert.Equal(1, Count(text, "别名内容")); Assert.DoesNotContain("旧媒体说明", text); Assert.Contains("后来完成的媒体说明", text);
            AssertClosedGroups(updated);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await ((IAgentHistory)store).GetTaskAsync(TestStore.Address(), admission.TaskId!.Value, deadline.Token))!.ReleasedAt is null) await Task.Delay(10, deadline.Token);
            Assert.Equal(2, calls); Assert.Single(fixture.Transport.Requests);
        }
        finally { release.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>前缀稳定、迟到确认、明确输入撤回和批量淘汰在连续协议请求中共同生效。</summary>
    [Theory]
    [InlineData(MessageVisibilityPolicy.Placeholder)]
    [InlineData(MessageVisibilityPolicy.MarkedOriginal)]
    [InlineData(MessageVisibilityPolicy.Original)]
    public async Task ConversationChangesUpdateOriginalProjectionWithoutBreakingToolGroups(MessageVisibilityPolicy visibility)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Channel<JsonElement> requests = Channel.CreateBounded<JsonElement>(4);
        TaskCompletionSource[] gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        int calls = 0;
        await using TestHttpServer server = await TestHttpServer.StartAsync(async context =>
        {
            using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            int index = Interlocked.Increment(ref calls) - 1;
            await requests.Writer.WriteAsync(document.RootElement.Clone(), context.RequestAborted);
            await gates[index].Task.WaitAsync(context.RequestAborted);
            await AgentCommunicationTests.RespondAsync(context, null, [(index < 3 ? "list_agents" : "finish_turn", new())]);
        }, token);
        string variable = "MISSILI_CONTEXT_UPDATES_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = ModelFailureTests.Configuration(server.BaseAddress, variable, ModelProtocol.ChatCompletions);
            configuration = configuration with
            {
                Models = configuration.Models.Select(model => model with { Tools = true }).ToImmutableArray(),
                Profiles = [configuration.Profiles[0] with { MaximumMessages = 6, DiscardMessages = 2, Visibility = visibility,
                    Agent = new AgentProfile { Enabled = true, Models = ["A"], Tools = [AgentToolIds.List, AgentToolIds.FinishTurn] } }]
            };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration, agentOptions: new AgentOptions { Enabled = true });
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token); fixture.Runtime.OpenAgents();
            IInfrastructureStore store = (IInfrastructureStore)fixture.Runtime.History;
            MessageSnapshot background = (await fixture.Runtime.ReceiveAsync(TestStore.Message("群内原始背景") with { SenderId = "another-user" }, token)).Message!;
            MessageSnapshot discardedIntent = await store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("淘汰后才确认的内容")] }, TestStore.Now, Guid.NewGuid(), token);
            MessageSnapshot late = await store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("窗口内迟到确认的内容")] }, TestStore.Now, Guid.NewGuid(), token);
            MessageSnapshot trigger = (await fixture.Runtime.ReceiveAsync(TestStore.Message("命令原始正文"), token)).Message!;
            AgentWakeResult admission = await fixture.Runtime.Agents.WakeAsync(new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = trigger.Id, CurrentContent = [SegmentValues.Text("处理器传播后的明确要求")] }, token);
            JsonElement first = await requests.Reader.ReadAsync(token);
            Assert.Contains("群内原始背景", Text(first));
            Assert.Contains("处理器传播后的明确要求", Text(first));
            Assert.DoesNotContain("命令原始正文", Text(first)); Assert.DoesNotContain("迟到确认", Text(first));
            gates[0].TrySetResult();
            JsonElement stable = await requests.Reader.ReadAsync(token);
            string[] prefix = first.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText()).ToArray();
            Assert.Equal(prefix, stable.GetProperty("messages").EnumerateArray().Take(prefix.Length).Select(message => message.GetRawText()));

            await store.SetVisibilityAsync(background.Id, true, false, token);
            await store.SetVisibilityAsync(trigger.Id, true, false, token);
            await store.SetDeliveryAsync(late.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "late-confirmed" }, token);
            await fixture.Runtime.ReceiveAsync(TestStore.Message("工具期间新增聊天"), token);
            gates[1].TrySetResult();
            JsonElement changed = await requests.Reader.ReadAsync(token);
            string changedText = Text(changed);
            Assert.Equal(visibility != MessageVisibilityPolicy.Placeholder, changedText.Contains("群内原始背景", StringComparison.Ordinal));
            Assert.Equal(visibility != MessageVisibilityPolicy.Placeholder, changedText.Contains("处理器传播后的明确要求", StringComparison.Ordinal));
            Assert.Equal(visibility != MessageVisibilityPolicy.Original, changedText.Contains("已撤回", StringComparison.Ordinal));
            Assert.Equal(1, Count(changedText, "窗口内迟到确认的内容"));
            Assert.Equal(1, Count(changedText, "工具期间新增聊天"));
            AssertClosedGroups(changed);

            for (int index = 0; index < 5; index++) await fixture.Runtime.ReceiveAsync(TestStore.Message("突发聊天-" + index), token);
            ConversationWindow evicted = await ((IModelStore)store).CaptureWindowAsync(TestStore.Address(), 6, 2, token);
            Assert.True(evicted.DiscardedThrough >= late.Id);
            await store.SetDeliveryAsync(discardedIntent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "after-eviction" }, token);
            gates[2].TrySetResult();
            JsonElement final = await requests.Reader.ReadAsync(token);
            string finalText = Text(final);
            Assert.DoesNotContain("群内原始背景", finalText);
            Assert.DoesNotContain("窗口内迟到确认的内容", finalText);
            Assert.DoesNotContain("淘汰后才确认的内容", finalText);
            Assert.DoesNotContain("处理器传播后的明确要求", finalText);
            AssertClosedGroups(final);
            gates[3].TrySetResult();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await ((IAgentHistory)store).GetTaskAsync(TestStore.Address(), admission.TaskId!.Value, deadline.Token))!.ReleasedAt is null) await Task.Delay(10, deadline.Token);
            Assert.Empty(fixture.Transport.Requests);
        }
        finally { foreach (TaskCompletionSource gate in gates) gate.TrySetResult(); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>每个请求的真实工具结果必须紧接请求；聊天更新只能发生在已闭合组之外。</summary>
    private static void AssertClosedGroups(JsonElement request)
    {
        JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();
        for (int index = 0; index < messages.Length; index++)
        {
            if (!messages[index].TryGetProperty("tool_calls", out JsonElement calls)) continue;
            string[] ids = calls.EnumerateArray().Select(call => call.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(ids, messages.Skip(index + 1).Take(ids.Length).Select(message => message.GetProperty("tool_call_id").GetString()));
        }
    }

    /// <summary>只展开真实文本，不比较 JSON 的转义表示。</summary>
    private static string Text(JsonElement request) => string.Join("\n", request.GetProperty("messages").EnumerateArray().Select(message =>
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind == JsonValueKind.Null) return "";
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
    }));

    /// <summary>来源只出现一次，明确输入不得再作为普通群历史重复编码。</summary>
    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
}
