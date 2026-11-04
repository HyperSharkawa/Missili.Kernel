using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Adapters.OneBot;

/// <summary>OneBot v11 规范到共享快照的映射，不访问网络或执行插件。
/// 基线：https://github.com/botuniverse/onebot-11/tree/d4456ee706f9ada9c2dfde56a2bcfc69752600e4 。</summary>
internal sealed class OneBotCodec
{
    private readonly OneBotOptions _options;
    /// <summary>保存此实例的身份和显式兼容选择。</summary>
    public OneBotCodec(OneBotOptions options) => _options = options;

    /// <summary>将完整消息事件转为历史输入，保留扩展字段但不重复保存原始帧。</summary>
    public IncomingMessage DecodeMessage(JsonElement root)
    {
        VerifyIdentity(root);
        string postType = String(root, "post_type");
        bool sent = postType == "message_sent";
        if (postType != "message" && !(sent && _options.CompatibilityProfile == CompatibilityProfile.SnowLuma)) throw new InvalidDataException("不支持的消息事件类别。");
        string kind = String(root, "message_type");
        if (kind is not ("group" or "private")) throw new InvalidDataException("消息会话类型无效。");
        string userId = Identity(root, "user_id");
        string conversation = kind == "group" ? Identity(root, "group_id") : userId;
        if (sent && kind == "private") conversation = Identity(root, "target_id");
        MessageAddress address = Address(kind, conversation);
        string platformId = Identity(root, "message_id");
        string[] normalized = ["self_id", "post_type", "message_type", "message_id", "user_id", "time", "message", "raw_message"];
        JsonElement metadata = Except(root, kind == "group" ? [.. normalized, "group_id"] : normalized);
        return new IncomingMessage
        {
            Address = address,
            SenderId = sent ? _options.BotAccountId : userId,
            PlatformMessageId = platformId,
            Direction = sent ? MessageDirection.Outgoing : MessageDirection.Incoming,
            OccurredAt = Timestamp(root, "time"),
            DeduplicationKey = JsonSerializer.Serialize(new[] { kind, conversation, sent ? "outgoing" : "incoming", platformId }),
            Metadata = metadata,
            Segments = DecodeSegments(root.GetProperty("message"), address)
        };
    }

    /// <summary>通知和请求保存规范化身份与剩余载荷，元事件由连接层消费。</summary>
    public IncomingPlatformEvent DecodeEvent(JsonElement root)
    {
        VerifyIdentity(root);
        string category = String(root, "post_type");
        if (category is not ("notice" or "request")) throw new InvalidDataException("不是业务通知或请求。");
        string type = String(root, category == "notice" ? "notice_type" : "request_type");
        string? subType = OptionalString(root, "sub_type");
        string? userId = OptionalIdentity(root, "user_id");
        string? groupId = OptionalIdentity(root, "group_id");
        bool recall = category == "notice" && type is "group_recall" or "friend_recall";
        // 标准已有的必要身份必须提供，不能伪造通知所属用户或会话。
        if ((type.StartsWith("group_", StringComparison.Ordinal) || type == "notify" || (category == "request" && type == "group")) && groupId is null)
            throw new InvalidDataException("群事件缺少群身份。");
        if (userId is null) throw new InvalidDataException("业务事件缺少用户身份。");
        if (category == "request") _ = String(root, "flag");
        return new IncomingPlatformEvent
        {
            AdapterId = _options.Id,
            PlatformId = "qq",
            BotAccountId = _options.BotAccountId,
            Category = category == "notice" ? PlatformEventCategory.Notice : PlatformEventCategory.Request,
            Type = type,
            SubType = subType,
            ConversationKind = groupId is null ? "private" : "group",
            ConversationId = groupId ?? userId,
            ActorId = OptionalIdentity(root, "operator_id") ?? userId,
            TargetUserId = OptionalIdentity(root, "target_id") ?? userId,
            TargetMessageId = recall ? Identity(root, "message_id") : OptionalIdentity(root, "message_id"),
            RecallsMessage = recall,
            OccurredAt = Timestamp(root, "time"),
            Payload = Except(root, "self_id", "post_type", "notice_type", "request_type", "sub_type", "time", "group_id", "operator_id", "target_id", "message_id")
        };
    }

    /// <summary>数组值是实际值，只有 CQ 字符串按规范执行一次转义解码。</summary>
    public ImmutableArray<IncomingSegment> DecodeSegments(JsonElement value, MessageAddress address)
    {
        if (value.ValueKind == JsonValueKind.String) return DecodeCq(value.GetString()!, address);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("消息内容必须为数组或 CQ 字符串。");
        ImmutableArray<IncomingSegment>.Builder result = ImmutableArray.CreateBuilder<IncomingSegment>();
        foreach (JsonElement segment in value.EnumerateArray()) result.Add(DecodeSegment(segment, address));
        return result.ToImmutable();
    }

    /// <summary>只在单段内隔离格式失败，其他正常段仍能持久化和执行。</summary>
    private IncomingSegment DecodeSegment(JsonElement segment, MessageAddress address)
    {
        string type = "invalid";
        string kind = "onebot11.invalid";
        JsonElement data = segment;
        try
        {
            type = String(segment, "type"); kind = Kind(type);
            data = segment.GetProperty("data");
            if (!Known(type)) return new IncomingSegment { Kind = kind, Payload = data.Clone(), ParseState = SegmentParseState.Unrecognized };
            if (data.ValueKind == JsonValueKind.Null && type is "rps" or "dice") data = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("消息段数据必须为对象。");
            bool capturedUrlOnlyImage = _options.CompatibilityProfile == CompatibilityProfile.SnowLuma && type == "image" &&
                !data.TryGetProperty("file", out _) && data.TryGetProperty("url", out JsonElement imageUrl) && imageUrl.ValueKind == JsonValueKind.String;
            if (!capturedUrlOnlyImage) ValidateData(type, data, sending: false);
            if (type == "text")
            {
                JsonElement extras = Except(data, "text");
                return new IncomingSegment { Kind = kind, Text = String(data, "text", allowEmpty: true), Payload = extras.EnumerateObject().Any() ? extras : null };
            }
            JsonElement payload = type switch { "at" => Rename(data, "qq", "userId"), "reply" => Rename(data, "id", "messageId"), _ => data.Clone() };
            ImmutableArray<IncomingMedia> media = [];
            if (type is "image" or "record" or "video")
            {
                string? file = OptionalString(data, "file");
                string? url = OptionalString(data, "url");
                MediaSource source;
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https") source = new MediaSource
                {
                    Kind = "http",
                    AdapterId = address.AdapterId,
                    BotAccountId = address.BotAccountId,
                    // HTTP 仍由宿主直接下载；保留文件身份供以后显式请求 WAV，不能把 URL 当作内容键。
                    Data = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["url"] = uri.AbsoluteUri, ["file"] = file, ["type"] = type })
                };
                else source = new MediaSource
                {
                    Kind = "onebot11.media",
                    AdapterId = address.AdapterId,
                    BotAccountId = address.BotAccountId,
                    Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["type"] = type, ["file"] = file ?? throw new InvalidDataException("媒体缺少有效来源。") })
                };
                media = [new IncomingMedia { Kind = type == "image" ? MediaKind.Image : type == "record" ? MediaKind.Audio : MediaKind.Video, Source = source }];
            }
            else if (type == "share" && OptionalString(data, "image") is string image && Uri.TryCreate(image, UriKind.Absolute, out Uri? thumbnail) && thumbnail.Scheme is "http" or "https")
                media = [new IncomingMedia { Kind = MediaKind.Image, Source = MediaSource.Http(thumbnail) }];
            return new IncomingSegment
            {
                Kind = kind,
                Payload = payload,
                Media = media,
                Content = type == "forward" ? new ContentSource { Kind = "forward", Address = address, PlatformId = String(data, "id") } : null
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return new IncomingSegment { Kind = kind, Payload = data.Clone(), ParseState = SegmentParseState.Invalid, ParseError = "OneBot 消息段字段或方向不符合约定。" }; }
    }

    /// <summary>CQ 按分隔符而非正则猜测切分，参数中的等号和转义只处理一层。</summary>
    private ImmutableArray<IncomingSegment> DecodeCq(string text, MessageAddress address)
    {
        ImmutableArray<IncomingSegment>.Builder result = ImmutableArray.CreateBuilder<IncomingSegment>();
        int position = 0;
        while (position < text.Length)
        {
            int start = text.IndexOf("[CQ:", position, StringComparison.Ordinal);
            if (start < 0) { AddText(text[position..]); break; }
            if (start > position) AddText(text[position..start]);
            int end = text.IndexOf(']', start + 4);
            if (end < 0)
            { result.Add(InvalidCq(text[start..])); break; }
            string[] parts = text[(start + 4)..end].Split(',');
            Dictionary<string, string> data = new(StringComparer.Ordinal);
            bool valid = !string.IsNullOrWhiteSpace(parts[0]) && !parts[0].Any(char.IsWhiteSpace);
            for (int index = 1; index < parts.Length; index++)
            {
                int equals = parts[index].IndexOf('=');
                if (equals <= 0 || parts[index][..equals].Any(char.IsWhiteSpace) || !data.TryAdd(parts[index][..equals], Unescape(parts[index][(equals + 1)..], true)))
                { valid = false; break; }
            }
            if (valid)
            {
                JsonElement segment = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["type"] = parts[0], ["data"] = data });
                result.Add(DecodeSegment(segment, address));
            }
            else result.Add(InvalidCq(text[start..(end + 1)]));
            position = end + 1;
        }
        if (text.Length == 0) AddText("");
        return result.ToImmutable();

        /// <summary>纯文本不解码逗号实体，避免与参数转义规则混淆。</summary>
        void AddText(string value) => result.Add(new IncomingSegment { Kind = SegmentTypes.Text, Text = Unescape(value, false) });
    }

    /// <summary>保存无法解析的 CQ 部分，展示说明不能成为命令输入。</summary>
    private static IncomingSegment InvalidCq(string raw) => new()
    {
        Kind = "onebot11.invalid",
        ParseState = SegmentParseState.Invalid,
        ParseError = "CQ 码结构无效。",
        Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["cq"] = raw })
    };

    /// <summary>先解码结构字符、最后解码和号，防止二次展开实体。</summary>
    private static string Unescape(string value, bool parameter)
    {
        string decoded = value.Replace("&#91;", "[", StringComparison.Ordinal).Replace("&#93;", "]", StringComparison.Ordinal);
        if (parameter) decoded = decoded.Replace("&#44;", ",", StringComparison.Ordinal);
        return decoded.Replace("&amp;", "&", StringComparison.Ordinal);
    }

    /// <summary>校验标准规定的必填字段与收发方向，额外字段原样保存。</summary>
    internal static void ValidateData(string type, JsonElement data, bool sending)
    {
        if ((!sending && type is "shake" or "anonymous" or "music" or "node") || (sending && type == "forward")) throw new InvalidDataException("消息段方向不受标准支持。");
        string[] required = type switch
        {
            "text" => ["text"],
            "face" or "reply" or "forward" => ["id"],
            "image" or "record" or "video" => ["file"],
            "at" => ["qq"],
            "poke" => ["type", "id"],
            "share" => ["url", "title"],
            "contact" => ["type", "id"],
            "location" => ["lat", "lon"],
            "xml" or "json" => ["data"],
            "music" => String(data, "type") == "custom" ? ["type", "url", "audio", "title"] : ["type", "id"],
            _ => []
        };
        foreach (string field in required) _ = String(data, field, allowEmpty: field is "text" or "data");
        if (type == "contact" && String(data, "type") is not ("qq" or "group")) throw new InvalidDataException("联系人类别无效。");
        if (type == "music" && String(data, "type") is not ("qq" or "163" or "xm" or "custom")) throw new InvalidDataException("音乐类别无效。");
        if (type == "location")
        {
            if (!double.TryParse(String(data, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
                !double.TryParse(String(data, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude) || !double.IsFinite(longitude) || longitude is < -180 or > 180)
                throw new InvalidDataException("位置坐标无效。");
        }
    }

    /// <summary>标准身份必须是 JSON 整数，通过十进制字符串保存避免浮点精度损失。</summary>
    internal static string Identity(JsonElement value, string name)
    { JsonElement field = value.GetProperty(name); return field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long number) ? number.ToString(CultureInfo.InvariantCulture) : throw new InvalidDataException("身份字段必须为整数：" + name); }
    /// <summary>可选身份不存在时返回空，存在但类型错误时明确失败。</summary>
    internal static string? OptionalIdentity(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) && field.ValueKind != JsonValueKind.Null ? Identity(value, name) : null;
    /// <summary>字段必须为实际字符串，不用 ToString 静默转换错误载荷。</summary>
    internal static string String(JsonElement value, string name, bool allowEmpty = false)
    { JsonElement field = value.GetProperty(name); return field.ValueKind == JsonValueKind.String && (allowEmpty || !string.IsNullOrWhiteSpace(field.GetString())) ? field.GetString()! : throw new InvalidDataException("字段必须为字符串：" + name); }
    /// <summary>可选字符串仍校验真实类型。</summary>
    internal static string? OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) && field.ValueKind != JsonValueKind.Null ? String(value, name, allowEmpty: true) : null;
    /// <summary>平台时间按 Unix 秒读取，内部公开接口保持带偏移时间。</summary>
    internal static DateTimeOffset Timestamp(JsonElement value, string name) => DateTimeOffset.FromUnixTimeSeconds(long.Parse(Identity(value, name), CultureInfo.InvariantCulture));
    /// <summary>连接身份与每条事件身份都需一致。</summary>
    public void VerifyIdentity(JsonElement value) { if (Identity(value, "self_id") != _options.BotAccountId) throw new InvalidDataException("事件账号与实例配置不一致。"); }
    /// <summary>使用已配置实例构造完整会话作用域。</summary>
    public MessageAddress Address(string kind, string id) => new() { AdapterId = _options.Id, PlatformId = "qq", BotAccountId = _options.BotAccountId, ConversationKind = kind, ConversationId = id };

    /// <summary>移除已经规范化的字段，不长期重复存储整份协议帧。</summary>
    internal static JsonElement Except(JsonElement source, params string[] names) => JsonSerializer.SerializeToElement(source.EnumerateObject()
        .Where(item => !names.Contains(item.Name, StringComparer.Ordinal)).ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal));
    /// <summary>规范字段使用共享名称；同名扩展留在显式扩展容器中。</summary>
    private static JsonElement Rename(JsonElement source, string oldName, string newName)
    {
        Dictionary<string, JsonElement> fields = source.EnumerateObject().Where(item => item.Name != oldName && item.Name != newName).ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
        if (source.TryGetProperty(newName, out JsonElement collision)) fields["onebotExtension"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [newName] = collision.Clone() });
        fields[newName] = source.GetProperty(oldName).Clone(); return JsonSerializer.SerializeToElement(fields);
    }
    /// <summary>标准功能列表用于区分未知扩展和已知格式错误。</summary>
    private static bool Known(string type) => type is "text" or "face" or "image" or "record" or "video" or "at" or "rps" or "dice" or "shake" or "poke" or "anonymous" or "share" or "contact" or "location" or "music" or "reply" or "forward" or "node" or "xml" or "json";
    /// <summary>协议名字映射到稳定语义类型，未识别类型保留协议命名空间。</summary>
    internal static string Kind(string type) => type switch
    {
        "record" => SegmentTypes.Audio,
        "at" => SegmentTypes.Mention,
        "face" or "contact" or "music" or "rps" or "dice" or "poke" or "shake" or "anonymous" => "qq." + type,
        "json" or "xml" or "node" => "onebot11." + type,
        "text" or "image" or "video" or "reply" or "share" or "location" or "forward" => type,
        _ => "onebot11." + type
    };
}
