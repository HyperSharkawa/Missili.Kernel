using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Missili.Abstractions;
using Missili.Persistence;
using Xunit;

namespace Missili.Tests;

/// <summary>实际 FTS5、短词查询、源更新触发器和旧库回填共同验证搜索契约。</summary>
public sealed class HistorySearchTests
{
    /// <summary>中英文、短词和标点都按字面查询，Unicode 会话身份不会因 JSON 转义串范围。</summary>
    [Theory]
    [InlineData("中文检索")]
    [InlineData("中文")]
    [InlineData("文")]
    [InlineData("alpha")]
    [InlineData("%_")]
    [InlineData("\"百分")]
    [InlineData("🐱中文")]
    public async Task LiteralSearchUsesOneConversation(string keyword)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        string body = "Alpha 中文检索 %_ \"百分 🐱中文";
        await fixture.Store.AppendAsync(TestStore.Message(body, conversation: "会话甲"), TestStore.Now, fixture.RunId, token);
        await fixture.Store.AppendAsync(TestStore.Message(body, conversation: "会话乙"), TestStore.Now, fixture.RunId, token);
        HistorySearchPage page = await fixture.Store.SearchAsync(new HistorySearchQuery { Conversation = TestStore.Address("会话甲"), Text = keyword }, token);
        HistorySearchHit hit = Assert.Single(page.Hits);
        Assert.Equal(body, hit.Text); Assert.Equal(HistoryRecordKind.Chat, hit.Kind); Assert.False(hit.Partial);
        Assert.Equal(TestStore.Address("会话甲"), (await fixture.Store.GetAsync(hit.MessageId!.Value, token))!.Address);
    }

    /// <summary>稳定游标与半开时间范围不重复、不漏页，发送者条件不会在客户端才过滤。</summary>
    [Fact]
    public async Task CursorTimeAndSenderFiltersRemainDeterministic()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        for (int index = 0; index < 25; index++) await fixture.Store.AppendAsync(TestStore.Message("分页查询 " + index) with { SenderId = index % 2 == 0 ? "selected" : "other" },
            TestStore.Now.AddSeconds(index), fixture.RunId, token);
        HistorySearchQuery query = new() { Conversation = TestStore.Address(), Text = "分页查询", SenderId = "selected", From = TestStore.Now.AddSeconds(4), Until = TestStore.Now.AddSeconds(22), PageSize = 4 };
        List<HistorySearchHit> hits = [];
        do
        {
            HistorySearchPage page = await fixture.Store.SearchAsync(query, token); hits.AddRange(page.Hits);
            query = query with { Before = page.Next };
        } while (query.Before is not null);
        Assert.Equal(9, hits.Count); Assert.Equal(9, hits.Select(hit => hit.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 9).Select(index => TestStore.Now.AddSeconds(20 - index * 2)), hits.Select(hit => hit.At));
    }

    /// <summary>撤回同时隐藏聊天与明确任务输入的命中片段，保留原文策略仍按请求生效。</summary>
    [Fact]
    public async Task RecallUpdatesChatAndTaskInputInsideSourceTransaction()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("原始任务内容"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult admission = await RegisterWakeAsync(fixture, message, [SegmentValues.Text("传播后的任务内容")], token);
        Assert.Single((await fixture.Store.SearchAsync(Query("原始任务"), token)).Hits);
        Assert.Equal(HistoryRecordKind.AgentInput, Assert.Single((await fixture.Store.SearchAsync(Query("传播后的"), token)).Hits).Kind);
        await fixture.Store.SetVisibilityAsync(message.Id, true, false, token);
        Assert.Empty((await fixture.Store.SearchAsync(Query("任务内容"), token)).Hits);
        HistorySearchPage original = await fixture.Store.SearchAsync(Query("任务内容") with { Visibility = MessageVisibilityPolicy.Original }, token);
        Assert.Equal(2, original.Hits.Length);
        Assert.All(original.Hits, hit => Assert.DoesNotContain("已撤回", hit.Text));
        HistorySearchPage marked = await fixture.Store.SearchAsync(Query("任务内容") with { Visibility = MessageVisibilityPolicy.MarkedOriginal }, token);
        Assert.All(marked.Hits, hit => Assert.True(hit.HiddenOriginal));
        Assert.Equal("原始任务内容", (await fixture.Store.GetAsync(message.Id, token))!.Segments[0].Text);
        Assert.NotNull(await fixture.Store.GetTaskAsync(TestStore.Address(), admission.TaskId!.Value, token));
    }

    /// <summary>未确认发送不当作聊天，迟到确认建立索引；成果只索引公开文字，不索引签名与原始模型记录。</summary>
    [Fact]
    public async Task LateConfirmationAndPublicResultsMaintainIndexWithoutNativeSecrets()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot outgoing = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("迟到确认正文")] }, TestStore.Now, fixture.RunId, token);
        Assert.Empty((await fixture.Store.SearchAsync(Query("迟到确认"), token)).Hits);
        await fixture.Store.SetDeliveryAsync(outgoing.Id, new TransportResult { State = DeliveryState.Unknown }, token);
        Assert.Empty((await fixture.Store.SearchAsync(Query("迟到确认"), token)).Hits);
        await fixture.Store.SetDeliveryAsync(outgoing.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "confirmed" }, token);
        Assert.Single((await fixture.Store.SearchAsync(Query("迟到确认"), token)).Hits);
        MessageSnapshot incoming = (await fixture.Store.AppendAsync(TestStore.Message("开始任务"), TestStore.Now, fixture.RunId, token)).Message!;
        AgentWakeResult admission = await RegisterWakeAsync(fixture, incoming, incoming.Segments, token);
        await fixture.Store.AppendEntryAsync(admission.AgentId!.Value, AgentEntryKind.ModelOutput, JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["signature"] = "不可搜索签名" }), TestStore.Now, token);
        await fixture.Store.CompleteTurnAsync(admission.AgentId.Value, new AgentResult
        {
            Complete = true,
            Content =
            [ModelBlock.TextBlock("公开成果正文"), new ModelBlock { Kind = ModelBlockKind.Native, Text = "不可搜索推理" }]
        }, TestStore.Now, false, token);
        HistorySearchHit hit = Assert.Single((await fixture.Store.SearchAsync(Query("公开成果"), token)).Hits);
        Assert.Equal(HistoryRecordKind.AgentResult, hit.Kind);
        Assert.Equal(admission.TaskId, hit.TaskId);
        Assert.Empty((await fixture.Store.SearchAsync(Query("不可搜索"), token)).Hits);
    }

    /// <summary>已有第三阶段库升级时回填真实聊天，长记录片段在命中位置附近且明确标记不完整。</summary>
    [Fact]
    public async Task MigrationBackfillsExistingMessagesAndSnippetSurroundsMatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using (MissiliDbContext database = Database(fixture))
        {
            string previous = database.Database.GetMigrations().ElementAt(2);
            await database.GetService<IMigrator>().MigrateAsync(previous, token);
        }
        string text = new string('前', 500) + "旧库搜索命中" + new string('后', 500);
        MessageSnapshot original = (await fixture.Store.AppendAsync(TestStore.Message(text), TestStore.Now, fixture.RunId, token)).Message!;
        await fixture.Store.InitializeAsync(Guid.NewGuid(), token);
        HistorySearchHit hit = Assert.Single((await fixture.Store.SearchAsync(Query("旧库搜索"), token)).Hits);
        Assert.True(hit.Partial); Assert.Contains("旧库搜索命中", hit.Text);
        Assert.StartsWith("…", hit.Text); Assert.EndsWith("…", hit.Text);
        Assert.Equal(text, (await fixture.Store.GetAsync(original.Id, token))!.Segments[0].Text);
        Assert.Equal(5, (await MigrationsAsync(fixture, token)).Length);
    }

    /// <summary>公开文本变化与索引同事务，回滚源更新不能留下已发布的命中。</summary>
    [Fact]
    public async Task RolledBackSourceMutationCannotLeakThroughSearch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot message = (await fixture.Store.AppendAsync(TestStore.Message("原来文字"), TestStore.Now, fixture.RunId, token)).Message!;
        await using (MissiliDbContext database = Database(fixture))
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(token);
            SegmentEntity segment = await database.MessageSegments.SingleAsync(item => item.MessageId == message.Id, token);
            segment.Text = "回滚的文字"; await database.SaveChangesAsync(token); await transaction.RollbackAsync(token);
        }
        Assert.Empty((await fixture.Store.SearchAsync(Query("回滚的"), token)).Hits);
        Assert.Single((await fixture.Store.SearchAsync(Query("原来文字"), token)).Hits);
    }

    /// <summary>创建明确输入用于验证同一来源的可见性，不启动真实模型。</summary>
    private static async Task<AgentWakeResult> RegisterWakeAsync(TestStore fixture, MessageSnapshot message, ImmutableArray<MessageSegment> input, CancellationToken token)
    {
        ConversationConfiguration configuration = ConversationConfigurationTests.Configuration(new Uri("http://127.0.0.1:1"));
        ConversationProfileSnapshot profile = new()
        { Revision = 1, Profile = configuration.Profiles[0], Models = configuration.Models.ToImmutableDictionary(model => model.Id), Providers = configuration.Providers.ToImmutableDictionary(provider => provider.Id) };
        return await fixture.Store.RegisterWakeAsync(new AgentWakeAdmission
        {
            WakeRequest = new AgentWakeRequest { Cause = TestStore.WakeCause, MessageId = message.Id, CurrentContent = input },
            Profile = profile,
            RunId = fixture.RunId,
            NewTaskId = Guid.NewGuid(),
            NewRootAgentId = Guid.NewGuid(),
            Now = TestStore.Now
        }, token);
    }

    /// <summary>普通搜索默认采用撤回占位策略与当前合成会话。</summary>
    private static HistorySearchQuery Query(string text) => new() { Conversation = TestStore.Address(), Text = text };

    /// <summary>管理测试连接不进入另一份连接池，避免独占临时文件清理时仍有句柄。</summary>
    private static MissiliDbContext Database(TestStore fixture) => new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(new SqliteConnectionStringBuilder
    { DataSource = fixture.DatabasePath, ForeignKeys = true, Pooling = false }.ToString()).Options);

    /// <summary>查询实际迁移历史，临时生成的增量迁移不能成为最终交付。</summary>
    private static async Task<string[]> MigrationsAsync(TestStore fixture, CancellationToken token)
    {
        await using MissiliDbContext database = Database(fixture);
        return (await database.Database.GetAppliedMigrationsAsync(token)).ToArray();
    }
}
