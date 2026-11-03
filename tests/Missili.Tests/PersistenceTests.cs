using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Missili.Abstractions;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 SQLite 集成测试，覆盖事务、稳定分页、编排和缓存状态。</summary>
public sealed class PersistenceTests
{
    /// <summary>时间依据、用户、会话和媒体类型在数据库中共同筛选，缺少发生时间不伪造值。</summary>
    [Fact]
    public async Task QueryFiltersTimeIdentityAndSegmentKind()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await fixture.Store.AppendAsync(TestStore.Message("early") with { OccurredAt = TestStore.Now.AddHours(-1) }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.AppendAsync(TestStore.Message("unknown") with { OccurredAt = null }, TestStore.Now.AddMinutes(1), fixture.RunId, token);
        ReceiveReceipt expected = await fixture.Store.AppendAsync(TestStore.MediaMessage(new Uri("http://127.0.0.1/media")) with
        { OccurredAt = TestStore.Now, SenderId = "selected" }, TestStore.Now.AddMinutes(2), fixture.RunId, token);
        await fixture.Store.AppendAsync(TestStore.Message("different-room", conversation: "other") with
        { OccurredAt = TestStore.Now, SenderId = "selected" }, TestStore.Now.AddMinutes(2), fixture.RunId, token);
        HistoryPage page = await fixture.Store.QueryAsync(TestStore.Query() with
        {
            Address = TestStore.Address(),
            SenderId = "selected",
            SegmentKind = SegmentKind.Image,
            Direction = MessageDirection.Incoming,
            Time = HistoryTime.Occurred,
            From = TestStore.Now,
            Until = TestStore.Now.AddMinutes(1)
        }, token);
        Assert.Equal(expected.Message!.Id, Assert.Single(page.Messages).Id);
        HistoryPage received = await fixture.Store.QueryAsync(TestStore.Query() with
        { Address = TestStore.Address(), From = TestStore.Now.AddMinutes(1), Until = TestStore.Now.AddMinutes(2) }, token);
        Assert.Equal("unknown", Assert.Single(received.Messages).Segments[0].Text);
        HistoryPage occurred = await fixture.Store.QueryAsync(TestStore.Query() with
        { Time = HistoryTime.Occurred, From = TestStore.Now, Until = TestStore.Now.AddMinutes(1) }, token);
        Assert.Equal(2, occurred.Messages.Length);
        Assert.All(occurred.Messages, message => Assert.NotNull(message.OccurredAt));
    }

    /// <summary>去重只依据稳定键，普通相同文字仍是独立消息。</summary>
    [Fact]
    public async Task DuplicateKeysDoNotCreateNewMessages()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        Task<ReceiveReceipt>[] requests = Enumerable.Range(0, 12)
            .Select(_ => fixture.Store.AppendAsync(TestStore.Message(key: "same"), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken)).ToArray();
        ReceiveReceipt[] receipts = await Task.WhenAll(requests);
        Assert.Single(receipts, item => !item.Duplicate);
        Assert.Single(receipts.Select(item => item.Message!.Id).Distinct());
        await fixture.Store.AppendAsync(TestStore.Message(), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        await fixture.Store.AppendAsync(TestStore.Message(), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        HistoryPage page = await fixture.Store.QueryAsync(TestStore.Query(), TestContext.Current.CancellationToken);
        Assert.Equal(3, page.Messages.Length);
    }

    /// <summary>同时间戳的前后翻页都不能漏消息或重复。</summary>
    [Theory]
    [InlineData(PageDirection.Forward)]
    [InlineData(PageDirection.Backward)]
    public async Task CursorIncludesIdForEqualTimestamps(PageDirection direction)
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        for (int index = 0; index < 13; index++)
            await fixture.Store.AppendAsync(TestStore.Message(index.ToString()), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        List<long> ids = [];
        HistoryQuery query = TestStore.Query(4) with { PageDirection = direction };
        // 只按返回游标继续，直到明确没有下一页。
        while (true)
        {
            HistoryPage page = await fixture.Store.QueryAsync(query, TestContext.Current.CancellationToken);
            ids.AddRange(page.Messages.Select(item => item.Id));
            if (page.NextCursor is null) break;
            query = query with { Cursor = page.NextCursor };
        }
        Assert.Equal(13, ids.Count);
        Assert.Equal(13, ids.Distinct().Count());
        Assert.Equal(direction == PageDirection.Forward ? ids.Order().ToArray() : ids.OrderDescending().ToArray(), ids);
    }

    /// <summary>撤回、删除与会话过滤不改变原始段内容。</summary>
    [Fact]
    public async Task VisibilityAndNeighborhoodPreserveOriginalContent()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        ReceiveReceipt first = await fixture.Store.AppendAsync(TestStore.Message("original"), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        await fixture.Store.AppendAsync(TestStore.Message("other", conversation: "elsewhere"), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        ReceiveReceipt last = await fixture.Store.AppendAsync(TestStore.Message("last"), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        await fixture.Store.SetVisibilityAsync(first.Message!.Id, true, true, TestContext.Current.CancellationToken);
        MessageSnapshot? stored = await fixture.Store.GetAsync(first.Message.Id, TestContext.Current.CancellationToken);
        Assert.Equal("original", stored!.Segments[0].Text);
        Assert.True(stored.Recalled && stored.Deleted);
        HistoryPage visible = await fixture.Store.QueryAsync(TestStore.Query() with
        { Address = TestStore.Address(), IncludeRecalled = false, IncludeDeleted = false }, TestContext.Current.CancellationToken);
        Assert.Equal(last.Message!.Id, Assert.Single(visible.Messages).Id);
        ImmutableArray<MessageSnapshot> around = await fixture.Store.GetAroundAsync(last.Message.Id, 5, 5, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { first.Message.Id, last.Message.Id }, around.Select(item => item.Id));
    }

    /// <summary>重复媒体消息被去重时，其引用也随事务回滚。</summary>
    [Fact]
    public async Task DuplicateMessageRollsBackItsMediaReferences()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        IncomingMessage input = TestStore.MediaMessage(new Uri("http://127.0.0.1/example")) with { DeduplicationKey = "image" };
        await fixture.Store.AppendAsync(input, TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        await fixture.Store.AppendAsync(input, TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        ImmutableArray<MediaWork> pending = await fixture.Store.ClaimMediaAsync(10, TestContext.Current.CancellationToken);
        Assert.Single(pending);
    }

    /// <summary>旧修订号不能覆盖已保存配置，停用并不会自行删除持久顺序。</summary>
    [Fact]
    public async Task HookOrderUsesCompareAndSwap()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        ImmutableArray<HandlerKey> keys = [new HandlerKey { PluginId = "a", HandlerId = "first" }, new HandlerKey { PluginId = "b", HandlerId = "second" }];
        StoredHookOrder saved = await fixture.Store.SaveHookOrderAsync(MessageHooks.Received, HandlerKind.Interceptor, keys, true, 0, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RevisionConflictException>(() => fixture.Store.SaveHookOrderAsync(MessageHooks.Received, HandlerKind.Interceptor, [], true, 0, TestContext.Current.CancellationToken));
        StoredHookOrder swapped = await fixture.Store.SaveHookOrderAsync(MessageHooks.Received, HandlerKind.Interceptor, keys.Reverse().ToImmutableArray(), true, saved.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(keys.Reverse(), swapped.Entries);
        await fixture.Store.InitializeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        StoredHookOrder persisted = await fixture.Store.GetHookOrderAsync(MessageHooks.Received, HandlerKind.Interceptor, TestContext.Current.CancellationToken);
        Assert.Equal(swapped.Revision, persisted.Revision);
        Assert.Equal(swapped.Customized, persisted.Customized);
        Assert.Equal(swapped.Entries.ToArray(), persisted.Entries.ToArray());
    }

    /// <summary>没有成功转述是正常状态，失败刷新不能覆盖已有缓存。</summary>
    [Fact]
    public async Task FailedTranscriptionRefreshKeepsSuccessfulText()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        ReceiveReceipt input = await fixture.Store.AppendAsync(TestStore.MediaMessage(new Uri("http://127.0.0.1/media")), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        Guid id = input.Message!.Segments[0].MediaId!.Value;
        await fixture.Store.ClaimMediaAsync(1, TestContext.Current.CancellationToken);
        string key = new('a', 64);
        await fixture.Store.CompleteMediaAsync(id, key, 3, "media/objects/aaa", TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Store.GetTranscriptionAsync(key, "image.description", TestContext.Current.CancellationToken));
        TranscriptionSnapshot success = await fixture.Store.SaveTranscriptionAsync(new TranscriptionSnapshot
        { ContentKey = key, Kind = "image.description", Text = "合成图片", Model = "synthetic", CompletedAt = TestStore.Now }, 0, TestContext.Current.CancellationToken);
        TranscriptionSnapshot failure = await fixture.Store.RecordTranscriptionFailureAsync(key, "image.description", "测试失败", success.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(success.Text, failure.Text);
        Assert.Equal("测试失败", failure.LastError);
        await Assert.ThrowsAsync<RevisionConflictException>(() => fixture.Store.SaveTranscriptionAsync(success, success.Revision, TestContext.Current.CancellationToken));
    }

    /// <summary>重启中断业务但恢复下载；结果未知不伪装为成功。</summary>
    [Fact]
    public async Task RestartInterruptsBusinessAndResetsOnlyUnfinishedMedia()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.MediaMessage(new Uri("http://127.0.0.1/media")), TestStore.Now, fixture.RunId, TestContext.Current.CancellationToken);
        await fixture.Store.ClaimMessagesAsync(fixture.RunId, 10, TestContext.Current.CancellationToken);
        await fixture.Store.ClaimMediaAsync(10, TestContext.Current.CancellationToken);
        await fixture.Store.InitializeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(ProcessingState.Interrupted, (await fixture.Store.GetAsync(message.Message!.Id, TestContext.Current.CancellationToken))!.Processing);
        Assert.Equal(MediaState.Pending, (await fixture.Store.GetMediaAsync(message.Message.Segments[0].MediaId!.Value, TestContext.Current.CancellationToken))!.Status.State);
        Assert.Empty(await fixture.Store.ClaimMessagesAsync(Guid.NewGuid(), 10, TestContext.Current.CancellationToken));
    }

    /// <summary>确认真实数据库拥有能够服务会话游标的组合索引。</summary>
    [Fact]
    public async Task ConversationSeekCanUseCompositeIndex()
    {
        await using TestStore fixture = await TestStore.CreateAsync();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT Id FROM Messages WHERE AdapterId=$a AND BotAccountId=$b AND PlatformId=$p AND ConversationKind=$k AND ConversationId=$c AND ReceivedAt >= $t ORDER BY ReceivedAt, Id LIMIT 20";
        command.Parameters.AddWithValue("$a", "test");
        command.Parameters.AddWithValue("$b", "bot");
        command.Parameters.AddWithValue("$p", "synthetic");
        command.Parameters.AddWithValue("$k", "group");
        command.Parameters.AddWithValue("$c", "room");
        command.Parameters.AddWithValue("$t", 0);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("IX_Messages_Conversation_Received", reader.GetString(3));
        SqliteConnection.ClearPool(connection);
    }
}
