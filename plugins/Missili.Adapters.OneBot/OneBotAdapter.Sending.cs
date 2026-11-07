using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>出站内容单独编码，只发送标准允许字段和方向，不泄露归档元数据。</summary>
internal sealed partial class OneBotAdapter
{
    /// <summary>固定当前连接后进行有限编码；编码期间断线不会改用新连接补发。</summary>
    public async Task<TransportResult> SendAsync(long messageId, OutgoingMessage message, CancellationToken cancellationToken)
    {
        OneBotSession? session;
        lock (_gate) session = ApiSession();
        if (session is not { Verified: true, CanCall: true }) return new TransportResult { State = DeliveryState.Failed, Error = "API 通道离线。" };
        if (message.Address.PlatformId != "qq" || message.Address.BotAccountId != _options.BotAccountId || message.Address.ConversationKind is not ("group" or "private"))
            return new TransportResult { State = DeliveryState.Failed, Error = "发送目标不属于此适配器支持的账号或会话。" };
        bool acquired = false;
        try
        {
            await _encoding.WaitAsync(cancellationToken).ConfigureAwait(false); acquired = true;
            EncodingBudget budget = new();
            List<Dictionary<string, object>> segments = await EncodeSegmentsAsync(message.Segments, budget, 1, cancellationToken).ConfigureAwait(false);
            string target = message.Address.ConversationKind == "group" ? "group_id" : "user_id";
            Dictionary<string, object> parameters = new() { [target] = message.Address.ConversationId, ["message"] = segments };
            JsonElement encoded = JsonSerializer.SerializeToElement(parameters);
            PlatformOperationResult result = await session.CallAsync(message.Address.ConversationKind == "group" ? "send_group_msg" : "send_private_msg",
                encoded, messageId, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            return OneBotSession.ToSendResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new TransportResult { State = DeliveryState.Cancelled, Error = "发送编码或排队已取消。" }; }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        { return new TransportResult { State = DeliveryState.Failed, Error = "发送内容无法编码：" + exception.GetType().Name }; }
        finally { if (acquired) _encoding.Release(); }
    }

    /// <summary>递归节点编码共享总媒体和段数额度，不能绕过根发送载荷限制。</summary>
    private async Task<List<Dictionary<string, object>>> EncodeSegmentsAsync(ImmutableArray<MessageSegment> segments, EncodingBudget budget, int depth, CancellationToken token)
    {
        if (depth > _options.MaxForwardDepth) throw new NotSupportedException("发送节点嵌套超过配置深度。");
        List<Dictionary<string, object>> result = [];
        foreach (MessageSegment segment in segments)
        {
            if (++budget.Segments > _options.MaxForwardSegments) throw new NotSupportedException("发送消息段数超过配置上限。");
            if (segment.Version != 1 || segment.ParseState != SegmentParseState.Parsed) throw new NotSupportedException("未解释或解析失败段不能直接发送。");
            string type = segment.Kind switch
            {
                SegmentTypes.Audio => "record",
                SegmentTypes.Mention => "at",
                SegmentTypes.Json => "json",
                SegmentTypes.Xml => "xml",
                _ when segment.Kind.StartsWith("qq.", StringComparison.Ordinal) => segment.Kind[3..],
                _ => segment.Kind
            };
            Dictionary<string, object> data = new(StringComparer.Ordinal);
            if (segment.Kind == SegmentTypes.Forward)
            {
                if (segment.Payload is null || !segment.Payload.Value.TryGetProperty("nodes", out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array)
                    throw new NotSupportedException("接收转发标识不能作为标准发送节点。");
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    if (++budget.Nodes > _options.MaxForwardNodes) throw new NotSupportedException("发送节点数超过配置上限。");
                    Dictionary<string, object> nodeData = new();
                    if (node.TryGetProperty("messageId", out JsonElement existing)) nodeData["id"] = existing.GetString() ?? throw new InvalidDataException("节点标识为空。");
                    else
                    {
                        nodeData["user_id"] = OneBotCodec.String(node, "userId"); nodeData["nickname"] = OneBotCodec.String(node, "nickname", true);
                        ImmutableArray<MessageSegment> nested = node.GetProperty("segments").Deserialize<ImmutableArray<MessageSegment>>();
                        nodeData["content"] = await EncodeSegmentsAsync(nested, budget, depth + 1, token).ConfigureAwait(false);
                    }
                    result.Add(new Dictionary<string, object> { ["type"] = "node", ["data"] = nodeData });
                }
                continue;
            }
            if (type == "text") data["text"] = segment.Text!;
            else if (type == "at") data["qq"] = segment.MentionTarget();
            else if (type == "reply") data["id"] = segment.ReplyTarget();
            else if (type is "image" or "record" or "video")
            {
                if (segment.MediaIds.Length != 1) throw new NotSupportedException("标准媒体段必须恰好关联一个原件。");
                MediaStatus status = await _context.Media.GetStatusAsync(segment.MediaIds[0], token).ConfigureAwait(false) ?? throw new InvalidDataException("媒体引用不存在。");
                if (status.State != MediaState.Available || status.Length is null || status.Length > _options.MaxOutgoingMediaBytes - budget.MediaBytes)
                    throw new InvalidDataException("媒体原件不可用或发送大小超过限制。");
                await using MediaReadResult media = await _context.Media.OpenReadAsync(status.Id, token).ConfigureAwait(false);
                if (media.Content is null) throw new InvalidDataException("媒体原件不可读取。");
                byte[] bytes = new byte[checked((int)status.Length.Value)];
                await media.Content.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                byte[] tail = new byte[1];
                if (await media.Content.ReadAsync(tail, token).ConfigureAwait(false) != 0) throw new InvalidDataException("媒体原件长度已改变。");
                budget.MediaBytes += bytes.Length;
                data["file"] = "base64://" + Convert.ToBase64String(bytes);
                CopyAllowed(segment.Payload, data, type == "image" ? ["type"] : type == "record" ? ["magic"] : []);
            }
            else
            {
                string[] fields = type switch
                {
                    "face" => ["id"],
                    "share" => ["url", "title", "content", "image"],
                    "contact" => ["type", "id"],
                    "location" => ["lat", "lon", "title", "content"],
                    "poke" => ["type", "id"],
                    "anonymous" => ["ignore"],
                    "music" => ["type", "id", "url", "audio", "title", "content", "image"],
                    "xml" or "json" => ["data"],
                    "rps" or "dice" or "shake" => [],
                    _ => throw new NotSupportedException("该消息段没有标准发送编码能力。")
                };
                CopyAllowed(segment.Payload, data, fields);
            }
            OneBotCodec.ValidateData(type, JsonSerializer.SerializeToElement(data), sending: true);
            result.Add(new Dictionary<string, object> { ["type"] = type, ["data"] = data });
        }
        return result;
    }

    /// <summary>显式白名单避免把接收快照中的内部状态和只读字段发送给平台。</summary>
    private static void CopyAllowed(JsonElement? payload, Dictionary<string, object> target, string[] fields)
    {
        if (payload is null) return;
        foreach (string field in fields) if (payload.Value.TryGetProperty(field, out JsonElement value)) target[field] = value.Clone();
    }

    /// <summary>一次编码共享累计额度，不按节点重新计算媒体上限。</summary>
    private sealed class EncodingBudget { public long MediaBytes { get; set; } public int Segments { get; set; } public int Nodes { get; set; } }
}
