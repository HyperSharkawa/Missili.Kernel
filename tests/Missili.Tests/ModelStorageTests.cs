using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Missili.Abstractions;
using Missili.Persistence;
using Xunit;

namespace Missili.Tests;

/// <summary>真实 SQLite 验证上下文边界和旧转述来源迁移，不用内存数据库代替。</summary>
public sealed class ModelStorageTests
{
    /// <summary>别名的较大规范 ID 不能使已经批量淘汰的逻辑消息复活，升级旧别名也保持这一规则。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasedLateReceiptDoesNotRestoreDiscardedMessage(bool upgrade)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot intent = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("已淘汰原文")] }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Confirmed }, token);
        await fixture.Store.AppendAsync(TestStore.Message("同批淘汰"), TestStore.Now, fixture.RunId, token);
        await fixture.Store.AppendAsync(TestStore.Message("保留正文"), TestStore.Now, fixture.RunId, token);
        Assert.Single((await fixture.Store.CaptureWindowAsync(TestStore.Address(), 3, 2, token)).Messages);
        MessageSnapshot observation = (await fixture.Store.AppendAsync(TestStore.Message("已淘汰原文") with
        { Direction = MessageDirection.Outgoing, PlatformMessageId = "late-alias", SenderId = TestStore.Address().BotAccountId }, TestStore.Now, fixture.RunId, token)).Message!;
        await fixture.Store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "late-alias" }, token);
        if (upgrade)
        {
            await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(new SqliteConnectionStringBuilder
            { DataSource = fixture.DatabasePath, ForeignKeys = true, Pooling = false }.ToString()).Options);
            IMigrator migrations = database.GetService<IMigrator>();
            await migrations.MigrateAsync("20261104002700_ModelContextAndCalls", token);
            await migrations.MigrateAsync(cancellationToken: token);
        }
        ConversationWindow window = await fixture.Store.CaptureWindowAsync(TestStore.Address(), 3, 2, token);
        Assert.DoesNotContain(window.Messages, message => message.Segments.Any(segment => segment.Text == "已淘汰原文"));
        Assert.Equal("保留正文", Assert.Single(window.Messages).Segments[0].Text);
        Assert.Equal(observation.Id, (await fixture.Store.GetAsync(intent.Id, token))!.Id);
        Assert.Equal("已淘汰原文", (await fixture.Store.GetAsync(observation.Id, token))!.Segments[0].Text);
    }

    /// <summary>突发历史按整批推进；用户显式选择 y=1 时也不超过窗口上限。</summary>
    [Theory]
    [InlineData(20, 97)]
    [InlineData(1, 99)]
    public async Task BurstBacklogUsesTheSameBatchRule(int discard, int retained)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        for (int index = 0; index < 237; index++) await fixture.Store.AppendAsync(TestStore.Message(index.ToString()), TestStore.Now, fixture.RunId, token);
        ConversationWindow window = await fixture.Store.CaptureWindowAsync(TestStore.Address(), 100, discard, token);
        Assert.Equal(retained, window.Messages.Length); Assert.Equal(237 - retained, window.DiscardedThrough);
    }

    /// <summary>达到上限整批淘汰，配置变大和重新打开数据库都不会恢复被丢弃的上下文。</summary>
    [Fact]
    public async Task WindowDiscardsWholeBatchesAndPersistsAcrossRestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        for (int index = 0; index < 99; index++) await fixture.Store.AppendAsync(TestStore.Message(index.ToString()), TestStore.Now, fixture.RunId, token);
        Assert.Equal(99, (await fixture.Store.CaptureWindowAsync(TestStore.Address(), 100, 20, token)).Messages.Length);
        await fixture.Store.AppendAsync(TestStore.Message("100"), TestStore.Now, fixture.RunId, token);
        ConversationWindow window = await fixture.Store.CaptureWindowAsync(TestStore.Address(), 100, 20, token);
        Assert.Equal(80, window.Messages.Length);
        Assert.Equal(20, window.DiscardedThrough);
        Assert.Equal(100, (await fixture.Store.QueryAsync(TestStore.Query(), token)).Messages.Length);
        await using SqliteStore restarted = new(fixture.DatabasePath, fixture.Options);
        await restarted.InitializeAsync(Guid.NewGuid(), token);
        ConversationWindow retained = await restarted.CaptureWindowAsync(TestStore.Address(), 200, 20, token);
        Assert.Equal(80, retained.Messages.Length);
        Assert.Equal(window.DiscardedThrough, retained.DiscardedThrough);
        Assert.Empty((await restarted.CaptureWindowAsync(TestStore.Address("different"), 100, 20, token)).Messages);
        Assert.Equal(35, (await restarted.CaptureWindowAsync(TestStore.Address(), 50, 15, token)).Messages.Length);
    }

    /// <summary>上下文只读取已确认出站记录，所有入站用户按完整会话范围隔离。</summary>
    [Fact]
    public async Task WindowIncludesAllMembersAndOnlyConfirmedOutgoing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await fixture.Store.AppendAsync(TestStore.Message("甲") with { SenderId = "one" }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.AppendAsync(TestStore.Message("乙") with { SenderId = "two" }, TestStore.Now, fixture.RunId, token);
        MessageSnapshot unknown = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("未知发送")] }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.SetDeliveryAsync(unknown.Id, new TransportResult { State = DeliveryState.Unknown }, token);
        MessageSnapshot sent = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage { Address = TestStore.Address(), Segments = [SegmentValues.Text("确认发送")] }, TestStore.Now, fixture.RunId, token);
        await fixture.Store.SetDeliveryAsync(sent.Id, new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "synthetic" }, token);
        ConversationWindow window = await fixture.Store.CaptureWindowAsync(TestStore.Address(), 100, 20, token);
        Assert.Equal(new[] { "one", "two", "bot" }, window.Messages.Select(item => item.SenderId));
    }

    /// <summary>旧单模型来源被完整转换为一个来源条目，成功文本和修订保持不变。</summary>
    [Fact]
    public async Task UpgradePreservesTranscriptionProvenance()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using MissiliDbContext database = new(new DbContextOptionsBuilder<MissiliDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, ForeignKeys = true }.ToString()).Options);
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261103133117_ExtensibleMessagesAndPlatformEvents", token);
        await database.Database.ExecuteSqlRawAsync("""
            INSERT INTO MediaBlobs(ContentKey,Length,RelativePath) VALUES('test',1,'media/test');
            INSERT INTO MediaTranscriptions(ContentKey,Kind,Text,Provider,Model,Prompt,Revision,CompletedAt)
            VALUES('test','image.description','旧文字','旧提供商','旧模型','旧提示',7,1000);
            """, token);
        await migrator.MigrateAsync(cancellationToken: token);
        TranscriptionSnapshot value = (await fixture.Store.GetTranscriptionAsync("test", "image.description", token))!;
        Assert.Equal("旧文字", value.Text);
        Assert.Equal(7, value.Revision);
        TranscriptionSource source = Assert.Single(value.Sources);
        Assert.Equal("旧提供商", source.Provider); Assert.Equal("旧模型", source.Model); Assert.Equal("旧提示", source.Prompt);
        Assert.False(database.Database.HasPendingModelChanges());
    }
}
