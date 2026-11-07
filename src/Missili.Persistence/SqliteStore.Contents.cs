using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Missili.Abstractions;

namespace Missili.Persistence;

/// <summary>通用结构化内容归档，节点媒体保持关系关联且可重复恢复。</summary>
public sealed partial class SqliteStore
{
    /// <summary>读取纯值内容和完整性，不根据读取自动重试远端请求。</summary>
    public Task<StructuredContentSnapshot?> GetContentAsync(Guid id, CancellationToken cancellationToken) => ReadAsync(database =>
    {
        StructuredContentEntity? row = database.StructuredContents.AsNoTracking().SingleOrDefault(item => item.Id == id);
        return row is null ? null : ContentSnapshot(row);
    }, cancellationToken);

    /// <summary>在单写入工作中复用相同来源，初次登记才创建待归档状态。</summary>
    public Task<Guid> RequestContentAsync(ContentSource source, DateTimeOffset now, CancellationToken cancellationToken)
    {
        source.Validate();
        return WriteAsync(database =>
        {
            StructuredContentEntity row = EnsureContent(database, source, now);
            if (source.Kind == "reply" && row.State == ContentState.Pending)
            {
                MessageEntity? local = WithSegments(InConversation(database.Messages, source.Address))
                    .Where(item => item.CanonicalId == null && item.PlatformMessageId == source.PlatformId).OrderBy(item => item.Id).FirstOrDefault();
                if (local is not null)
                {
                    MessageSnapshot message = Snapshot(local);
                    row.NodesJson = JsonSerializer.Serialize(new[] { new ContentNode { Path = "0", SenderId = message.SenderId,
                        OccurredAt = message.OccurredAt, Metadata = message.Metadata, Segments = message.Segments } });
                    for (int index = 0; index < message.Segments.Length; index++)
                    {
                        for (int ordinal = 0; ordinal < message.Segments[index].MediaIds.Length; ordinal++)
                            database.MediaBindings.Add(new MediaBindingEntity { ContentId = row.Id, Path = "0/segments/" + index, Ordinal = ordinal, MediaId = message.Segments[index].MediaIds[ordinal] });
                    }
                    row.State = ContentState.Complete; row.Revision++;
                }
            }
            database.SaveChanges(); return row.Id;
        }, cancellationToken);
    }

    /// <summary>从持久积压领取有限批次，处理中状态由启动恢复。</summary>
    public Task<ImmutableArray<StructuredContentSnapshot>> ClaimContentsAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return WriteAsync(database =>
        {
            List<StructuredContentEntity> rows = database.StructuredContents.Where(item => !item.Claimed && item.State == ContentState.Pending)
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(count).ToList();
            foreach (StructuredContentEntity row in rows) { row.State = ContentState.Expanding; row.Claimed = true; }
            database.SaveChanges();
            return rows.Select(ContentSnapshot).ToImmutableArray();
        }, cancellationToken);
    }

    /// <summary>按稳定节点位置合并已取得内容，保存媒体关联后才推进恢复检查点。</summary>
    public Task<StructuredContentSnapshot> SaveContentAsync(Guid id, ContentExpansion progress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(progress.State) || progress.Nodes.IsDefault || progress.Issues.IsDefault) throw new ArgumentException("归档进度无效。");
        return WriteAsync(database =>
        {
            StructuredContentEntity row = database.StructuredContents.Single(item => item.Id == id);
            ContentSource source = JsonSerializer.Deserialize<ContentSource>(row.SourceJson)!;
            List<ContentNode> nodes = JsonSerializer.Deserialize<List<ContentNode>>(row.NodesJson)!;
            Dictionary<string, ContentNode> known = nodes.ToDictionary(item => item.Path, StringComparer.Ordinal);
            List<MediaBindingEntity> bindings = database.MediaBindings.Where(item => item.ContentId == id).ToList();
            HashSet<string> supplied = new(StringComparer.Ordinal);
            foreach (IncomingContentNode node in progress.Nodes)
            {
                ContentNode saved = ConvertNode(node);
                if (known.ContainsKey(node.Path)) nodes[nodes.FindIndex(item => item.Path == node.Path)] = saved;
                else nodes.Add(saved);
                known[node.Path] = saved;
            }
            row.NodesJson = JsonSerializer.Serialize(nodes);
            row.IssuesJson = JsonSerializer.Serialize(progress.Issues);
            row.CheckpointJson = progress.Checkpoint?.GetRawText();
            row.State = progress.State;
            row.Claimed = progress.State == ContentState.Expanding;
            row.Revision++;
            database.SaveChanges();
            return ContentSnapshot(row);

            /// <summary>节点内每个媒体位置只创建一次引用，进度重存不会重复下载。</summary>
            ContentNode ConvertNode(IncomingContentNode node)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(node.Path);
                if (!supplied.Add(node.Path)) throw new ArgumentException("归档节点位置重复。");
                ImmutableArray<MessageSegment>.Builder segments = ImmutableArray.CreateBuilder<MessageSegment>();
                for (int index = 0; index < node.Segments.Length; index++)
                {
                    IncomingSegment segment = node.Segments[index].Freeze();
                    ContentValidation.ValidateSegment(segment.Kind, segment.Version, segment.Text, segment.Payload, segment.ParseState);
                    string path = node.Path + "/segments/" + index;
                    ImmutableArray<Guid>.Builder media = ImmutableArray.CreateBuilder<Guid>();
                    for (int ordinal = 0; ordinal < segment.Media.Length; ordinal++)
                    {
                        MediaBindingEntity? binding = bindings.SingleOrDefault(item => item.Path == path && item.Ordinal == ordinal);
                        if (binding is null)
                        {
                            binding = new MediaBindingEntity
                            {
                                ContentId = id,
                                Path = path,
                                Ordinal = ordinal,
                                MediaId = AddMedia(database, segment.Media[ordinal], source.Address, now)
                            };
                            database.MediaBindings.Add(binding); bindings.Add(binding);
                        }
                        media.Add(binding.MediaId);
                    }
                    segments.Add(new MessageSegment
                    {
                        Kind = segment.Kind,
                        Version = segment.Version,
                        Text = segment.Text,
                        Payload = segment.Payload,
                        ParseState = segment.ParseState,
                        ParseError = segment.ParseError,
                        MediaIds = media.ToImmutable()
                    });
                }
                return new ContentNode
                {
                    Path = node.Path,
                    SenderId = node.SenderId,
                    SenderName = node.SenderName,
                    OccurredAt = node.OccurredAt,
                    Metadata = node.Metadata?.Clone(),
                    Segments = segments.ToImmutable(),
                    Children = node.Children.Select(ConvertNode).ToImmutableArray()
                };
            }
        }, cancellationToken);
    }

    /// <summary>保存等待和局部失败；失败根仍可读取已经成功的节点。</summary>
    public Task SetContentStateAsync(Guid id, ContentState state, ContentIssue? issue, CancellationToken cancellationToken) => WriteAsync(database =>
    {
        StructuredContentEntity row = database.StructuredContents.Single(item => item.Id == id);
        if (issue is not null)
        {
            List<ContentIssue> issues = JsonSerializer.Deserialize<List<ContentIssue>>(row.IssuesJson)!;
            issues.Add(issue); row.IssuesJson = JsonSerializer.Serialize(issues);
        }
        row.State = state; row.Claimed = false; row.Revision++;
        database.SaveChanges(); return true;
    }, cancellationToken);

    /// <summary>目录变化时恢复等待项，不复活显式失败或已清理媒体。</summary>
    public Task ResumeAdapterArchivesAsync(CancellationToken cancellationToken) => WriteAsync(database =>
    {
        database.MediaReferences.Where(item => item.State == MediaState.WaitingForAdapter)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.State, MediaState.Pending).SetProperty(item => item.Claimed, false));
        database.StructuredContents.Where(item => item.State == ContentState.WaitingForAdapter)
            .ExecuteUpdate(setters => setters.SetProperty(item => item.State, ContentState.Pending).SetProperty(item => item.Claimed, false));
        return true;
    }, cancellationToken);

    /// <summary>JSON 解码后返回共享契约值，不加载平台或插件专有 CLR 类型。</summary>
    private static StructuredContentSnapshot ContentSnapshot(StructuredContentEntity row) => new()
    {
        Id = row.Id,
        Source = JsonSerializer.Deserialize<ContentSource>(row.SourceJson)!,
        State = row.State,
        Revision = row.Revision,
        Nodes = JsonSerializer.Deserialize<ImmutableArray<ContentNode>>(row.NodesJson),
        Issues = JsonSerializer.Deserialize<ImmutableArray<ContentIssue>>(row.IssuesJson),
        Checkpoint = ParseJson(row.CheckpointJson)
    };
}
