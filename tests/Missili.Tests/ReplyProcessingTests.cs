using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Missili.Abstractions;
using Missili.Kernel;
using Missili.Nexus;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Missili.Tests;

/// <summary>独立官方后处理 DLL 通过生产加载、配置和出站路径验收。</summary>
public sealed class ReplyProcessingTests
{
    private const string PluginId = "missili.plugins.reply-processing";

    /// <summary>默认无规则不变换文本，显式规则只作用于真实文本段并保留混合顺序。</summary>
    [Fact]
    public async Task DefaultAndMixedContentPreserveStructureAndUnicode()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ConversationConfiguration configuration = Configuration("{}");
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: configuration);
        await LoadAsync(fixture, token);
        string text = "甲👨‍👩‍👧‍👦é乙。\n\n尾部很长的句子！结束";
        MessageSegment mention = new() { Kind = SegmentTypes.Mention, Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["target"] = "member" }) };
        using Image<Rgba32> picture = new(1, 1);
        Guid mediaId = await ImagePreparationTests.ImportAsync(fixture, picture, "PNG", token);
        MessageSegment image = new() { Kind = SegmentTypes.Image, MediaIds = [mediaId] };
        OutgoingBatch original = OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text(text), mention, image, SegmentValues.Text("末尾👩🏽‍💻")]);
        BatchSendResult unmodified = await fixture.Runtime.Sender.SendAsync(original, token);
        Assert.True(unmodified.Confirmed, unmodified.Error);
        Assert.Equal(text, fixture.Transport.Requests.Single().Segments[0].Text);
        configuration = Configuration("""{"steps":[{"type":"replace","pattern":"甲","replacement":"替"},{"type":"split","maximumTextElements":6}]}""");
        await ((ConversationConfigurationService)fixture.Runtime.Profiles).PublishAsync(configuration, token);
        BatchSendResult sent = await fixture.Runtime.Sender.SendAsync(original with { Id = Guid.NewGuid() }, token);
        Assert.True(sent.Confirmed, sent.Error);
        OutgoingMessage[] parts = fixture.Transport.Requests.Skip(1).ToArray();
        Assert.True(parts.Length > 1);
        Assert.All(parts, part => Assert.InRange(part.Segments.Where(segment => segment.Kind == SegmentTypes.Text).Sum(segment => new StringInfo(segment.Text!).LengthInTextElements), 0, 6));
        MessageSegment[] flat = parts.SelectMany(part => part.Segments).ToArray();
        Assert.Equal(text.Replace("甲", "替", StringComparison.Ordinal) + "末尾👩🏽‍💻", string.Concat(flat.Select(segment => segment.Text)));
        Assert.Single(flat, segment => segment.Kind == SegmentTypes.Mention);
        Assert.Single(flat, segment => segment.Kind == SegmentTypes.Image);
        Assert.True(Array.FindIndex(flat, segment => segment.Kind == SegmentTypes.Mention) < Array.FindIndex(flat, segment => segment.Kind == SegmentTypes.Image));
        Assert.Contains(flat, segment => segment.Text?.Contains("👨‍👩‍👧‍👦", StringComparison.Ordinal) == true);
        Assert.Contains(flat, segment => segment.Text?.Contains("é", StringComparison.Ordinal) == true);
        Assert.Contains(flat, segment => segment.Text?.Contains("👩🏽‍💻", StringComparison.Ordinal) == true);
    }

    /// <summary>多个自然边界同时存在时使用段落优先级，切分不修剪或复制字符。</summary>
    [Fact]
    public async Task ParagraphBoundaryWinsAndProcessorOrderIsUserDefined()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(
            """{"steps":[{"type":"split","maximumTextElements":10},{"type":"replace","pattern":"^","replacement":"前"}]}"""));
        await LoadAsync(fixture, token);
        BatchSendResult result = await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text("甲\n\n乙。丙\n丁戊己庚辛")]), token);
        Assert.True(result.Confirmed, result.Error);
        Assert.Equal("前甲\n\n", fixture.Transport.Requests.First().Segments[0].Text);
        Assert.All(fixture.Transport.Requests, message => Assert.StartsWith("前", message.Segments[0].Text));
    }

    /// <summary>已加载插件拒绝无效更新并保留旧修订，停用时配置可以保留但启用必须再验证。</summary>
    [Theory]
    [InlineData("""{"steps":[{"type":"replace","pattern":"[","replacement":"x"}]}""")]
    [InlineData("""{"steps":[{"type":"split","maximumTextElements":"2"}]}""")]
    [InlineData("""{"steps":[{"type":"split","maximumTextElements":2,"maximumTextElements":3}]}""")]
    [InlineData("""{"steps":[{"type":"split","unknown":true}]}""")]
    public async Task InvalidUpdatesDoNotPublishOrActivate(string invalid)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration("{}"));
        string package = await LoadAsync(fixture, token);
        ConversationConfigurationService profiles = (ConversationConfigurationService)fixture.Runtime.Profiles;
        long revision = profiles.Resolve(TestStore.Address()).Revision;
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.PublishAsync(Configuration(invalid), token));
        Assert.Equal(revision, profiles.Resolve(TestStore.Address()).Revision);
        await fixture.Runtime.Plugins.StopAsync(PluginId, token);
        await profiles.PublishAsync(Configuration(invalid), token);
        Assert.Equal(PluginState.Faulted, (await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token)).State);
        Assert.Equal(BatchSendState.Failed, (await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text("原文")]), token)).State);
        Assert.Empty(fixture.Transport.Requests);
    }

    /// <summary>来源以宿主实际插件身份筛选，普通状态用途默认不处理。</summary>
    [Fact]
    public async Task PurposeAndActualPluginSourceControlSelection()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration(
            """{"sourcePlugins":["sample"],"steps":[{"type":"replace","pattern":"原文","replacement":"替换"}]}"""));
        await LoadAsync(fixture, token);
        string source = await fixture.PackageAsync("sample", "v1", token);
        await fixture.Runtime.Plugins.LoadAsync(source, RuntimeFixture.Configuration(), token);
        ReceiveReceipt input = await fixture.Runtime.ReceiveAsync(TestStore.Message("原文"), token);
        await fixture.FinishedAsync(input.Message!.Id, token);
        Assert.Contains("替换", fixture.Transport.Requests.Single().Segments[0].Text);
        await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text("原文")], "status"), token);
        Assert.Equal("原文", fixture.Transport.Requests.Last().Segments[0].Text);
        OutgoingMessage sent = fixture.Transport.Requests.First();
        OutgoingBatchSnapshot record = (await ((IOutgoingStore)fixture.Runtime.History).GetBatchAsync(sent.Address, sent.BatchId!.Value, token))!;
        Assert.Equal("sample", record.Original.Origin.PluginId);
    }

    /// <summary>处理超出最终批次数量时整批拒绝，不截掉尾部后发送。</summary>
    [Fact]
    public async Task OversizeSplitFailsWithoutSendingAndReloadCollectsAssembly()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(token, models: Configuration("""{"steps":[{"type":"split","maximumTextElements":1}]}"""));
        string package = await LoadAsync(fixture, token);
        Assert.Equal(BatchSendState.Failed, (await fixture.Runtime.Sender.SendAsync(OutgoingBatch.Single(TestStore.Address(), [SegmentValues.Text(new string('甲', 17))]), token)).State);
        Assert.Empty(fixture.Transport.Requests);
        Assert.Equal(PluginState.Active, (await fixture.Runtime.Plugins.ReloadAsync(PluginId, package, RuntimeFixture.Configuration(), token)).State);
        WeakReference reference = ((PluginManager)fixture.Runtime.Plugins).GetLastUnloadReference(PluginId)!;
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        Assert.False(reference.IsAlive);
    }

    /// <summary>复制已构建的独立官方包，Host 和测试都不静态加载插件实现。</summary>
    private static async Task<string> LoadAsync(RuntimeFixture fixture, CancellationToken token)
    {
        string package = OfficialPluginPackages.GetPath("reply-processing");
        PluginStatus result = await fixture.Runtime.Plugins.LoadAsync(package, RuntimeFixture.Configuration(), token);
        Assert.Equal(PluginState.Active, result.State);
        return package;
    }

    /// <summary>配置档只引用插件命名空间，仍复用同一模型和会话目录。</summary>
    private static ConversationConfiguration Configuration(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ConversationConfiguration configuration = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1/v1"));
        return configuration with
        {
            Profiles = [configuration.Profiles[0] with
        {
            Plugins = ImmutableDictionary<string, JsonElement>.Empty.Add(PluginId, document.RootElement.Clone()),
            Outgoing = new OutgoingOptions { Processors = [new HandlerKey { PluginId = PluginId, HandlerId = "process" }] }
        }]
        };
    }
}
