using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Model.Mock;
using Xunit;

namespace Missili.Tests;

/// <summary>审批使用真实模型服务和协议连接，合法拒绝与运行失败不能混为一谈。</summary>
public sealed class ToolApprovalTests
{
    /// <summary>完整 JSON 放行或拒绝后即结束，非法输出也不会请求另一个模型碰运气。</summary>
    [Theory]
    [InlineData("{\"decision\":\"allow\",\"reason\":\"合成读取已获授权\"}", ToolApprovalState.Allowed)]
    [InlineData("{\"decision\":\"deny\",\"reason\":\"需要发起者确认目标\"}", ToolApprovalState.Denied)]
    [InlineData("{\"decision\":\"ask\",\"reason\":\"请补充\"}", ToolApprovalState.Failed)]
    [InlineData("{\"decision\":\"allow\",\"decision\":\"deny\",\"reason\":\"重复字段\"}", ToolApprovalState.Failed)]
    [InlineData("{\"decision\":\"allow\",\"reason\":\"\"}", ToolApprovalState.Failed)]
    [InlineData("不是 JSON", ToolApprovalState.Failed)]
    public async Task CompleteDecisionNeverStartsAnotherReviewer(string text, ToolApprovalState expected)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { StrictSteps = true, Steps = [new ModelMockStep { Text = text }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_APPROVAL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            ToolApprovalResult result = await fixture.Runtime.Approvals.ReviewAsync(Request(fixture), token);
            Assert.Equal(expected, result.State);
            Assert.Equal(expected == ToolApprovalState.Allowed, result.Permitted);
            Assert.Equal(1, mock.TotalRequests);
            Assert.NotNull(result.ModelCallId);
            ModelCallRecord call = (await fixture.Runtime.Models.Calls.GetCallAsync(result.ModelCallId.Value, token))!;
            Assert.Equal("tool_approval", call.Purpose);
            Assert.False(mock.Requests[0].Body!.Value.TryGetProperty("tools", out JsonElement unused));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>协议故障可切换审批候选，有效结论只有一次且保留前次失败记录。</summary>
    [Fact]
    public async Task ReviewerTransportFailureUsesConfiguredFallback()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { StrictSteps = true, Steps = [new ModelMockStep { Status = 500 }, new ModelMockStep { Text = "{\"decision\":\"allow\",\"reason\":\"已核对范围\"}" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_APPROVAL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            ToolApprovalResult result = await fixture.Runtime.Approvals.ReviewAsync(Request(fixture), token);
            Assert.Equal(ToolApprovalState.Allowed, result.State);
            Assert.Equal(new[] { "A", "B" }, mock.Requests.Select(request => request.Model));
            ModelCallRecord call = (await fixture.Runtime.Models.Calls.GetCallAsync(result.ModelCallId!.Value, token))!;
            Assert.NotNull(call.Attempts[0].DetailId);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>逐工具禁用零请求，自定义政策替换全局政策，相关变化才使旧批准失效。</summary>
    [Fact]
    public async Task OverridesReplacePolicyAndIgnoreUnrelatedRevisions()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { Steps = [new ModelMockStep { Text = "{\"decision\":\"allow\",\"reason\":\"符合专用政策\"}" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_APPROVAL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            ConversationConfiguration configuration = Configuration(mock.BaseAddress, variable);
            ToolApprovalOptions approvals = configuration.Profiles[0].Approvals with
            {
                Tools = ImmutableDictionary<string, ToolApprovalOverride>.Empty
                    .Add("fixture.read", new ToolApprovalOverride { Enabled = false })
                    .Add("fixture.write", new ToolApprovalOverride { Models = ["B"], Policy = new ToolApprovalPolicy { CustomText = "本工具独立政策正文。" } })
            };
            configuration = configuration with { Profiles = [configuration.Profiles[0] with { Approvals = approvals }] };
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            ToolApprovalRequest request = Request(fixture);
            Assert.Equal(ToolApprovalState.NotRequired, (await fixture.Runtime.Approvals.ReviewAsync(request, token)).State);
            Assert.Equal(0, mock.TotalRequests);
            request = request with { ToolId = "fixture.write" };
            Assert.Equal(ToolApprovalState.Allowed, (await fixture.Runtime.Approvals.ReviewAsync(request, token)).State);
            Assert.Equal("B", Assert.Single(mock.Requests).Model);
            JsonElement systemContent = mock.Requests[0].Body!.Value.GetProperty("messages")[0].GetProperty("content");
            string instructions = systemContent.ValueKind == JsonValueKind.String ? systemContent.GetString()!
                : string.Concat(systemContent.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            Assert.Contains("本工具独立政策正文。", instructions);
            Assert.DoesNotContain("一、审核具体动作", instructions);
            ConversationProfileSnapshot unrelated = request.Profile with
            {
                Revision = request.Profile.Revision + 1,
                Profile = request.Profile.Profile with { MaximumMessages = 200, Execution = request.Profile.Profile.Execution with { CaptureSuccessfulDetails = true } }
            };
            Assert.True(fixture.Runtime.Approvals.IsCurrent(request, unrelated));
            ToolApprovalOptions changed = approvals with { Tools = approvals.Tools.SetItem("fixture.write", new ToolApprovalOverride { Models = ["A"] }) };
            Assert.False(fixture.Runtime.Approvals.IsCurrent(request, unrelated with { Profile = unrelated.Profile with { Approvals = changed } }));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>取消正在等待响应头的审批，不请求备用候选，不生成有效放行。</summary>
    [Fact]
    public async Task CancellationStopsReviewWithoutFallback()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ModelMockServer mock = await ModelMockServer.StartAsync(new ModelMockScenario
        { Steps = [new ModelMockStep { WaitForSignal = "release" }] }, "http://127.0.0.1:0", token);
        string variable = "MISSILI_APPROVAL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "synthetic");
        try
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(mock.BaseAddress, variable));
            await ModelMediaTests.LoadProviderAsync(fixture, ModelProtocol.ChatCompletions, token);
            using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<ToolApprovalResult> review = fixture.Runtime.Approvals.ReviewAsync(Request(fixture), cancel.Token);
            await mock.NextRequestAsync(token);
            await cancel.CancelAsync();
            Assert.Equal(ToolApprovalState.Cancelled, (await review.WaitAsync(TimeSpan.FromSeconds(10), token)).State);
            Assert.Equal(1, mock.TotalRequests);
        }
        finally { mock.Release("release"); Environment.SetEnvironmentVariable(variable, null); }
    }

    /// <summary>仅本次审批证据涉及的管理员身份变化使批准失效，无关成员变动不影响资格。</summary>
    [Fact]
    public async Task ApprovalBindsRelevantCurrentAdministratorFacts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration configuration = Configuration(new Uri("http://127.0.0.1:1"), "synthetic-unneeded");
        configuration = configuration with { Profiles = [configuration.Profiles[0] with { Agent = new AgentProfile { Administrators = ["operator"] } }] };
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
        ToolApprovalRequest request = Request(fixture) with { Evidence = [new ToolApprovalEvidence { Kind = "user_message", SenderId = "operator",
            Content = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["configured_administrator"] = true }) }] };
        ConversationProfileSnapshot unrelated = request.Profile with { Revision = request.Profile.Revision + 1,
            Profile = request.Profile.Profile with { Agent = request.Profile.Profile.Agent with { Administrators = ["operator", "another"] } } };
        Assert.True(fixture.Runtime.Approvals.IsCurrent(request, unrelated));
        Assert.False(fixture.Runtime.Approvals.IsCurrent(request, unrelated with
        { Profile = unrelated.Profile with { Agent = unrelated.Profile.Agent with { Administrators = ["another"] } } }));
    }

    /// <summary>测试动作参数与身份都是共享数据，完整用户证据保持独立数据层。</summary>
    private static ToolApprovalRequest Request(RuntimeFixture fixture) => new()
    {
        ToolId = "fixture.read",
        Description = "读取合成数据",
        Generation = Guid.NewGuid(),
        PermissionCategory = "test.read",
        Prepared = new PreparedToolCall { Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["target"] = "synthetic" }), ExecutionDescription = "只读合成对象。" },
        Conversation = TestStore.Address(),
        Profile = fixture.Runtime.Profiles.Resolve(TestStore.Address()),
        Origin = new OperationOrigin { PluginId = "fixture" },
        Evidence = [new ToolApprovalEvidence { Kind = "user_message", SenderId = "synthetic-user", Content = JsonSerializer.SerializeToElement("请读取合成数据。") }]
    };

    /// <summary>审批使用两个有序候选，所有连接均指向本测试的回环服务。</summary>
    private static ConversationConfiguration Configuration(Uri address, string variable)
    {
        ConversationConfiguration configuration = ModelFailureTests.Configuration(address, variable, ModelProtocol.ChatCompletions);
        return configuration with { Profiles = [configuration.Profiles[0] with { Approvals = new ToolApprovalOptions { Enabled = true, Models = ["A", "B"] } }] };
    }
}
