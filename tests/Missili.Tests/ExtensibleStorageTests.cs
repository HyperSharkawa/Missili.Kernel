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

/// <summary>验证扩展存储迁移与协议身份语义，数据均为合成样本。</summary>
public sealed class ExtensibleStorageTests
{
    /// <summary>真实旧结构的数据经过升级、降级和再次升级后仍可按原标识读取。</summary>
    [Fact]
    public async Task LegacyDataSurvivesMigrationRoundTrip()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        await using MissiliDbContext database = Context(fixture);
        IMigrator migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261102214402_InitialInfrastructure", token);
        Guid mediaId = Guid.NewGuid();
        string key = new('a', 64);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Messages (Id, AdapterId, PlatformId, BotAccountId, ConversationKind, ConversationId,
                SenderId, PlatformMessageId, ReceivedAt, OccurredAt, Direction, Recalled, Deleted, Processing, Delivery, RunId)
            VALUES (42,'test','synthetic','bot','group','room','user','old',123,100,0,1,0,2,0,{fixture.RunId});
            INSERT INTO MediaReferences (Id, Kind, Source, SourceKey, State, CreatedAt, Claimed, ContentKey, Length, RelativePath)
            VALUES ({mediaId},1,'https://example.invalid/media','["test","bot","source"]',3,123,0,{key},17,'media/objects/legacy');
            INSERT INTO MessageSegments (MessageId, Position, Kind, Text, MediaId) VALUES (42,0,0,'旧文本',NULL),(42,1,1,NULL,{mediaId});
            INSERT INTO MediaBlobs (ContentKey,Length,RelativePath) VALUES ({key},17,'media/objects/legacy');
            INSERT INTO MediaTranscriptions (ContentKey,Kind,Text,Revision) VALUES ({key},'image.description','旧转述',3);
            INSERT INTO HookOrders (HookId,Kind,Revision,Customized) VALUES ('message.received',2,4,1);
            INSERT INTO HookOrderEntries (HookId,Kind,PluginId,HandlerId,Position) VALUES ('message.received',2,'test','listener',0);
            """, token);
        // 两轮均读取真实新模型，避免只验证迁移没有抛异常。
        for (int pass = 0; pass < 2; pass++)
        {
            await migrator.MigrateAsync(cancellationToken: token);
            MessageSnapshot message = (await fixture.Store.GetAsync(42, token))!;
            Assert.Equal("旧文本", message.Segments[0].Text);
            Assert.True(message.Recalled);
            Assert.Equal(123, message.ReceivedAt.ToUnixTimeMilliseconds());
            Assert.Equal(SegmentTypes.Image, message.Segments[1].Kind);
            Assert.Equal(1, message.Segments[1].Version);
            Assert.Equal(mediaId, Assert.Single(message.Segments[1].MediaIds));
            MediaWork media = (await fixture.Store.GetMediaAsync(mediaId, token))!;
            Assert.Equal("https://example.invalid/media", media.Source.Data.GetProperty("url").GetString());
            Assert.Equal("source", media.Source.StableKey);
            Assert.Equal("media/objects/legacy", media.RelativePath);
            Assert.Equal(key, media.Status.ContentKey);
            Assert.Equal("旧转述", (await fixture.Store.GetTranscriptionAsync(key, "image.description", token))!.Text);
            Assert.Equal(4, (await fixture.Store.GetHookOrderAsync(MessageHooks.Received, HandlerKind.Listener, token)).Revision);
            if (pass == 0) await migrator.MigrateAsync("20261102214402_InitialInfrastructure", token);
        }
    }

    /// <summary>新数据不能降级为旧模型时，在任何数据删除前拒绝操作。</summary>
    [Fact]
    public async Task UnsupportedDowngradePreservesExtendedData()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        ReceiveReceipt received = await fixture.Store.AppendAsync(TestStore.Message() with
        { Segments = [new IncomingSegment { Kind = "onebot11.future", ParseState = SegmentParseState.Unrecognized, Payload = JsonSerializer.SerializeToElement(new { value = 9 }) }] }, TestStore.Now, fixture.RunId, token);
        await using MissiliDbContext database = Context(fixture);
        await Assert.ThrowsAsync<SqliteException>(() => database.GetService<IMigrator>().MigrateAsync("20261102214402_InitialInfrastructure", token));
        MessageSnapshot stored = (await fixture.Store.GetAsync(received.Message!.Id, token))!;
        Assert.Equal(9, stored.Segments[0].Payload!.Value.GetProperty("value").GetInt32());
        Assert.Equal(2, (await database.Database.GetAppliedMigrationsAsync(token)).Count());
    }

    /// <summary>未知载荷、解析失败和多媒体关联同时保存，释放来源文档不影响历史。</summary>
    [Fact]
    public async Task ExtendedSegmentsKeepPayloadAndRelationalMedia()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        using JsonDocument data = JsonDocument.Parse("{\"custom\":[1,2],\"text\":123}");
        IncomingMedia media = new() { Kind = MediaKind.Image, Source = MediaSource.Http(new Uri("https://example.invalid/a")) };
        ReceiveReceipt received = await fixture.Store.AppendAsync(TestStore.Message() with
        {
            Segments = [new IncomingSegment { Kind = "onebot11.future", Payload = data.RootElement, ParseState = SegmentParseState.Unrecognized, Media = [media, media] },
                new IncomingSegment { Kind = SegmentTypes.Text, Payload = data.RootElement, ParseState = SegmentParseState.Invalid, ParseError = "文本字段不是字符串" },
                new IncomingSegment { Kind = SegmentTypes.Text, Text = "实际正文" }]
        }, TestStore.Now, fixture.RunId, token);
        data.Dispose();
        MessageSnapshot stored = (await fixture.Store.GetAsync(received.Message!.Id, token))!;
        Assert.Equal(2, stored.Segments[0].MediaIds.Distinct().Count());
        Assert.Equal(2, stored.Segments[0].Payload!.Value.GetProperty("custom").GetArrayLength());
        Assert.Null(stored.Segments[1].Text);
        Assert.Equal(SegmentParseState.Invalid, stored.Segments[1].ParseState);
        Assert.Equal("实际正文", stored.Segments[2].Text);
        Assert.Equal(2, (await fixture.Store.ClaimMediaAsync(10, token)).Length);
        Assert.Single((await fixture.Store.QueryAsync(TestStore.Query() with { SegmentType = "onebot11.future" }, token)).Messages);
    }

    /// <summary>撤回不要求原消息先到；其他会话和账号同号消息不受影响。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecallMatchesExactScopeRegardlessOfArrivalOrder(bool recallFirst)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        IncomingPlatformEvent recall = new()
        {
            AdapterId = "test",
            PlatformId = "synthetic",
            BotAccountId = "bot",
            Type = "group_recall",
            Category = PlatformEventCategory.Notice,
            ConversationKind = "group",
            ConversationId = "room",
            TargetMessageId = "9",
            RecallsMessage = true
        };
        if (recallFirst) await fixture.Store.AppendEventAsync(recall, TestStore.Now, fixture.RunId, token);
        ReceiveReceipt message = await fixture.Store.AppendAsync(TestStore.Message() with { PlatformMessageId = "9" }, TestStore.Now, fixture.RunId, token);
        ReceiveReceipt other = await fixture.Store.AppendAsync(TestStore.Message(conversation: "other") with { PlatformMessageId = "9" }, TestStore.Now, fixture.RunId, token);
        if (!recallFirst) await fixture.Store.AppendEventAsync(recall, TestStore.Now, fixture.RunId, token);
        await fixture.Store.AppendEventAsync(recall, TestStore.Now, fixture.RunId, token);
        Assert.True((await fixture.Store.GetAsync(message.Message!.Id, token))!.Recalled);
        Assert.False((await fixture.Store.GetAsync(other.Message!.Id, token))!.Recalled);
        Assert.Equal(2, (await fixture.Store.QueryEventsAsync(new EventQuery { Type = "group_recall" }, token)).Events.Length);
    }

    /// <summary>相同平台身份的自发上报和回执保留一条规范记录，两个本地标识仍能查询。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnMessageAndReceiptHaveOneCanonicalHistory(bool observationFirst)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestStore fixture = await TestStore.CreateAsync();
        MessageSnapshot intent = await fixture.Store.AppendOutgoingAsync(new OutgoingMessage
        { Address = TestStore.Address(), Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = "发送内容" }] }, TestStore.Now, fixture.RunId, token);
        TransportResult confirmation = new() { State = DeliveryState.Confirmed, PlatformMessageId = "99" };
        if (!observationFirst) await fixture.Store.SetDeliveryAsync(intent.Id, confirmation, token);
        ReceiveReceipt observed = await fixture.Store.AppendAsync(TestStore.Message("发送内容") with
        { Direction = MessageDirection.Outgoing, PlatformMessageId = "99", SenderId = "bot" }, TestStore.Now, fixture.RunId, token);
        if (observationFirst) await fixture.Store.SetDeliveryAsync(intent.Id, confirmation, token);
        await fixture.Store.SetDeliveryAsync(intent.Id, new TransportResult { State = DeliveryState.Unknown }, token);
        MessageSnapshot canonical = Assert.Single((await fixture.Store.QueryAsync(TestStore.Query(), token)).Messages);
        Assert.Equal(observed.Message!.Id, canonical.Id);
        Assert.Equal(DeliveryState.Confirmed, canonical.Delivery);
        Assert.Equal(canonical.Id, (await fixture.Store.GetAsync(intent.Id, token))!.Id);
        ImmutableArray<MessageSnapshot> notices = await fixture.Store.ClaimMessagesAsync(fixture.RunId, 10, token);
        Assert.Single(notices);
        await fixture.Store.SetProcessingAsync(canonical.Id, ProcessingState.Finished, token);
        await fixture.Store.SetDeliveryAsync(intent.Id, confirmation, token);
        Assert.Empty(await fixture.Store.ClaimMessagesAsync(fixture.RunId, 10, token));
    }

    /// <summary>迁移测试使用独立上下文，存储工作器不共享同一个上下文。</summary>
    private static MissiliDbContext Context(TestStore fixture) => new(new DbContextOptionsBuilder<MissiliDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, ForeignKeys = true }.ToString()).Options);
}
