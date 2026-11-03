using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>合并转发与引用补全，网络查询的每一步都有持久化进度。</summary>
internal sealed partial class OneBotAdapter
{
    /// <summary>根转发采用有界迭代展开，部分失败保留已取得节点，不创建节点后台任务。</summary>
    public async Task ExpandContentAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        if (current.Source.Kind == "reply") { await ExpandReplyAsync(current, save, cancellationToken).ConfigureAwait(false); return; }
        if (current.Source.Kind != "forward") throw new NotSupportedException("结构化内容来源不支持。");
        ExpansionCheckpoint checkpoint = current.Checkpoint.HasValue
            ? current.Checkpoint.Value.Deserialize(OneBotJsonContext.Default.ExpansionCheckpoint)!
            : new ExpansionCheckpoint { Pending = [new ForwardWork { Id = current.Source.PlatformId, Path = "$", Depth = 1, Ancestors = [] }] };
        List<ContentIssue> issues = current.Issues.ToList();
        while (checkpoint.Pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForwardWork work = checkpoint.Pending[0];
            if (checkpoint.Requests >= _options.MaxForwardRequests)
            {
                foreach (ForwardWork pending in checkpoint.Pending) issues.Add(Issue(pending.Path, "request-limit", "达到远端查询次数限制。"));
                checkpoint.Pending.Clear(); await SaveAsync([], ContentState.Limited).ConfigureAwait(false); return;
            }
            checkpoint.Requests++;
            await SaveAsync([], ContentState.Expanding).ConfigureAwait(false);
            PlatformOperationResult response = await InvokeAsync("get_forward_msg", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["id"] = work.Id }), cancellationToken).ConfigureAwait(false);
            if (response.State == PlatformOperationState.Unavailable || (response.State == PlatformOperationState.Unknown && !GetStatus().ApiAvailable)) throw new AdapterUnavailableException();
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint.Pending.RemoveAt(0);
            List<IncomingContentNode> nodes = [];
            try
            {
                if (response.State != PlatformOperationState.Succeeded || response.Data is null) throw new InvalidDataException("转发查询失败。");
                JsonElement data = response.Data.Value;
                bool compatible = _options.CompatibilityProfile == CompatibilityProfile.SnowLuma && data.TryGetProperty("messages", out _);
                JsonElement array = data.GetProperty(compatible ? "messages" : "message");
                if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("转发节点必须为数组。");
                int position = 0;
                List<ForwardWork> children = [];
                foreach (JsonElement wireNode in array.EnumerateArray())
                {
                    string path = work.Path + "/nodes/" + position++;
                    if (checkpoint.Nodes >= _options.MaxForwardNodes)
                    { issues.Add(Issue(path, "node-limit", "达到转发节点数限制，此处及后续节点未归档。")); break; }
                    try
                    {
                        JsonElement payload = compatible ? wireNode : wireNode.GetProperty("data");
                        if (!compatible && OneBotCodec.String(wireNode, "type") != "node") throw new InvalidDataException("转发结果包含非节点段。");
                        JsonElement message = payload.GetProperty(compatible ? "message" : "content");
                        ImmutableArray<IncomingSegment> segments = _codec.DecodeSegments(message, current.Source.Address);
                        if (checkpoint.Segments + segments.Length > _options.MaxForwardSegments)
                        { issues.Add(Issue(path, "segment-limit", "达到转发消息段数限制，此处及后续节点未归档。")); break; }
                        string? senderId, senderName;
                        if (compatible)
                        {
                            JsonElement sender = payload.GetProperty("sender");
                            senderId = OneBotCodec.OptionalIdentity(sender, "user_id"); senderName = OneBotCodec.OptionalString(sender, "nickname");
                        }
                        else { senderId = OneBotCodec.String(payload, "user_id"); senderName = OneBotCodec.String(payload, "nickname", true); }
                        IncomingContentNode node = new()
                        {
                            Path = path,
                            SenderId = senderId,
                            SenderName = senderName,
                            Segments = segments,
                            OccurredAt = payload.TryGetProperty("time", out _) ? OneBotCodec.Timestamp(payload, "time") : null,
                            Metadata = OneBotCodec.Except(payload, "message", "content", "user_id", "nickname", "time")
                        };
                        int bytes = JsonSerializer.SerializeToUtf8Bytes(node).Length;
                        if (checkpoint.Bytes + bytes > _options.MaxForwardBytes)
                        { issues.Add(Issue(path, "content-limit", "达到转发规范化内容大小限制，此处及后续节点未归档。")); break; }
                        checkpoint.Bytes += bytes; checkpoint.Nodes++; checkpoint.Segments += segments.Length; nodes.Add(node);
                        for (int index = 0; index < segments.Length; index++)
                        {
                            ContentSource? child = segments[index].Content;
                            if (child is null) continue;
                            string childPath = path + "/segments/" + index;
                            if (work.Ancestors.Contains(child.PlatformId, StringComparer.Ordinal) || child.PlatformId == work.Id)
                            { issues.Add(Issue(childPath, "cycle", "转发存在循环引用，此分支停止展开。")); continue; }
                            if (work.Depth >= _options.MaxForwardDepth)
                            { issues.Add(Issue(childPath, "depth-limit", "达到转发展开深度限制。")); continue; }
                            children.Add(new ForwardWork { Id = child.PlatformId, Path = childPath, Depth = work.Depth + 1, Ancestors = [.. work.Ancestors, work.Id] });
                        }
                    }
                    catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException or JsonException)
                    { issues.Add(Issue(path, "node-invalid", "此转发节点结构无效，其他节点继续保存。")); }
                }
                checkpoint.Pending.InsertRange(0, children);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or KeyNotFoundException)
            { issues.Add(Issue(work.Path, "fetch-failed", "此部分转发获取失败。")); }
            ContentState state = checkpoint.Pending.Count > 0 ? ContentState.Expanding : issues.Count == 0 ? ContentState.Complete
                : issues.Any(item => item.Code.EndsWith("-limit", StringComparison.Ordinal)) ? ContentState.Limited : checkpoint.Nodes == 0 ? ContentState.Failed : ContentState.Partial;
            await SaveAsync(nodes.ToImmutableArray(), state).ConfigureAwait(false);
        }

        /// <summary>检查点只使用插件自己的源生成元数据，节点采用共享契约序列化。</summary>
        Task SaveAsync(ImmutableArray<IncomingContentNode> nodes, ContentState state) => save(new ContentExpansion
        { Nodes = nodes, State = state, Issues = issues.ToImmutableArray(), Checkpoint = JsonSerializer.SerializeToElement(checkpoint, OneBotJsonContext.Default.ExpansionCheckpoint) }, cancellationToken);
    }

    /// <summary>显式补取引用目标，结果仅归档，不作为新收到的消息分发。</summary>
    private async Task ExpandReplyAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken token)
    {
        PlatformOperationResult response = await InvokeAsync("get_msg", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["message_id"] = current.Source.PlatformId }), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response.State == PlatformOperationState.Unavailable || (response.State == PlatformOperationState.Unknown && !GetStatus().ApiAvailable)) throw new AdapterUnavailableException();
        if (response.State != PlatformOperationState.Succeeded || response.Data is null) throw new InvalidDataException("引用消息无法获取。");
        JsonElement data = response.Data.Value;
        if (OneBotCodec.Identity(data, "message_id") != current.Source.PlatformId || OneBotCodec.String(data, "message_type") != current.Source.Address.ConversationKind)
            throw new InvalidDataException("引用查询返回的身份不匹配。");
        if (data.TryGetProperty("group_id", out _) && OneBotCodec.Identity(data, "group_id") != current.Source.Address.ConversationId)
            throw new InvalidDataException("引用查询返回的会话不匹配。");
        JsonElement sender = data.GetProperty("sender");
        IncomingContentNode node = new()
        {
            Path = "0",
            SenderId = OneBotCodec.OptionalIdentity(sender, "user_id"),
            SenderName = OneBotCodec.OptionalString(sender, "nickname"),
            OccurredAt = OneBotCodec.Timestamp(data, "time"),
            Metadata = OneBotCodec.Except(data, "message", "time", "message_id", "message_type"),
            Segments = _codec.DecodeSegments(data.GetProperty("message"), current.Source.Address)
        };
        await save(new ContentExpansion { State = ContentState.Complete, Nodes = [node] }, token).ConfigureAwait(false);
    }

    /// <summary>局部问题只形成完整性说明，不成为节点原始文本。</summary>
    private static ContentIssue Issue(string path, string code, string description) => new() { Path = path, Code = code, Description = description };
}

/// <summary>持久检查点仅记录尚未展开的引用和累计预算，恢复不重放已存节点。</summary>
internal sealed class ExpansionCheckpoint
{
    public List<ForwardWork> Pending { get; set; } = [];
    public int Requests { get; set; }
    public int Nodes { get; set; }
    public int Segments { get; set; }
    public long Bytes { get; set; }
}
/// <summary>一个有稳定位置的子引用，祖先身份仅用于当前路径循环检测。</summary>
internal sealed class ForwardWork
{
    public required string Id { get; set; }
    public required string Path { get; set; }
    public int Depth { get; set; }
    public string[] Ancestors { get; set; } = [];
}
