using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>媒体与结构化内容工具实际消费已有归档和转述服务，不创建第二套任务机制。</summary>
public sealed partial class AgentRuntime
{
    /// <summary>媒体读取属于普通工作，转述沿用模型分阶段期限，等待控制工具仍独立。</summary>
    private static AgentToolDefinition AssetDefinition(string id, string name, string description, string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return new AgentToolDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Parameters = document.RootElement.Clone(),
            OutputDescription = "返回 JSON 文本分页及真实完整性；next_offset 可继续读取。失败不清除旧成功结果。",
            PermissionCategory = id == AgentToolIds.ReadTranscription ? "media.transcription" : "media.read",
            ExecutionTimeout = id == AgentToolIds.ReadTranscription ? null : TimeSpan.FromSeconds(180),
            DeclaredPermissions = id == AgentToolIds.ReadTranscription ? [AgentPermissions.RefreshTranscription] : []
        };
    }

    /// <summary>准备只检查已有来源和缓存版本，明确冻结是否生成及是否刷新。</summary>
    private static async Task<PreparedToolCall> PrepareAssetAsync(AgentToolContext context, string tool, JsonElement input, CancellationToken token)
    {
        Fields(input, tool == AgentToolIds.ReadContent ? ["content_id", "wait", "offset", "length"] : ["media_id", "generate", "refresh", "offset", "length"]);
        string field = tool == AgentToolIds.ReadContent ? "content_id" : "media_id";
        if (!Guid.TryParse(RequiredText(input, field), out Guid id) || id == Guid.Empty) return new PreparedToolCall { Error = "归档身份无效。" };
        int offset = PageNumber(input, "offset", 0, 0, int.MaxValue), length = PageNumber(input, "length", 8192, 1, 65536);
        bool refresh = Flag(input, "refresh"), generate = Flag(input, "generate") || refresh, wait = Flag(input, "wait");
        ConversationAssetKind kind = tool == AgentToolIds.ReadContent ? ConversationAssetKind.Content : ConversationAssetKind.Media;
        if (!await context.Assets.CanReadAsync(context.Conversation, kind, id, context.Profile.Profile.Visibility, token).ConfigureAwait(false))
            return new PreparedToolCall { Error = "当前获准范围内没有该归档。" };
        Dictionary<string, object> actual = new() { [field] = id, ["offset"] = offset, ["length"] = length };
        if (tool == AgentToolIds.ReadContent) actual["wait"] = wait;
        else
        {
            MediaStatus? media = await context.Media.GetStatusAsync(id, token).ConfigureAwait(false);
            if (media?.Kind is not (MediaKind.Image or MediaKind.Audio)) return new PreparedToolCall { Error = "仅图片和语音支持转述，视频仍使用标题与类型占位。" };
            TranscriptionSnapshot? cached = await context.Transcriptions.GetAsync(id, token).ConfigureAwait(false);
            actual["generate"] = generate; actual["refresh"] = refresh; actual["expected_revision"] = cached?.Revision ?? 0;
        }
        return new PreparedToolCall
        {
            Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["tool"] = tool, ["arguments"] = actual }),
            RequiredPermissions = refresh ? [AgentPermissions.RefreshTranscription] : [],
            ExecutionDescription = tool == AgentToolIds.ReadContent ? "只读取当前会话已有结构化归档及其失败说明。" : refresh ? "重新转述指定原件，成功才替换当前成功缓存。" : generate ? "缺少成功缓存时显式执行转述。" : "只读取当前成功缓存，不执行模型。"
        };
    }

    /// <summary>读取原件内容状态或执行明确转述，返回的旧缓存与本次失败分别表达。</summary>
    private static async Task<ToolExecutionResult> ReadAssetAsync(AgentToolContext context, string tool, JsonElement input, CancellationToken token)
    {
        int offset = input.GetProperty("offset").GetInt32(), length = input.GetProperty("length").GetInt32();
        try
        {
            if (tool == AgentToolIds.ReadContent)
            {
                Guid id = input.GetProperty("content_id").GetGuid();
                StructuredContentSnapshot? content = input.GetProperty("wait").GetBoolean()
                    ? await context.Contents.WaitContentAsync(id, token).ConfigureAwait(false) : await context.Contents.GetContentAsync(id, token).ConfigureAwait(false);
                return content is null ? Rejected("当前获准范围内没有该归档。")
                    : ToolResultPages.Json(JsonSerializer.SerializeToElement(content), offset, length, context.MaximumResultBytes);
            }
            Guid media = input.GetProperty("media_id").GetGuid();
            bool refresh = input.GetProperty("refresh").GetBoolean();
            TranscriptionSnapshot? cached = await context.Transcriptions.GetAsync(media, token).ConfigureAwait(false);
            if (!input.GetProperty("generate").GetBoolean()) return cached?.Text is null
                ? new ToolExecutionResult { State = ToolCallState.Unavailable, ErrorKind = "CacheMissing", Error = "暂无成功转述；需要转述时显式指定 generate=true。" }
                : ToolResultPages.Json(JsonSerializer.SerializeToElement(cached), offset, length, context.MaximumResultBytes);
            if (refresh && (cached?.Revision ?? 0) != input.GetProperty("expected_revision").GetInt64()) return Rejected("审批后转述缓存版本已变化，需要重新提交刷新请求。");
            MediaTranscriptionResult result = await context.Transcriptions.EnsureAsync(media, context.Profile, refresh, token).ConfigureAwait(false);
            ToolExecutionResult page = ToolResultPages.Json(JsonSerializer.SerializeToElement(result), offset, length, context.MaximumResultBytes);
            return page with { State = result.Succeeded ? page.State : ToolCallState.Failed, Complete = result.Succeeded, Error = result.Error, ErrorKind = result.Succeeded ? page.ErrorKind : "Transcription" };
        }
        catch (KeyNotFoundException) { return Rejected("当前获准范围内没有该归档。"); }
    }

    /// <summary>分页数值不接受字符串、小数或越界值。</summary>
    private static int PageNumber(JsonElement input, string name, int fallback, int minimum, int maximum)
    {
        if (!input.TryGetProperty(name, out JsonElement value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= minimum && number <= maximum ? number : throw new ArgumentException(name + " 必须是允许范围内的整数。");
    }

    /// <summary>只有实际布尔值才能开启等待、生成或刷新。</summary>
    private static bool Flag(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out JsonElement value)) return false;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ArgumentException(name + " 必须是布尔值。");
    }
}
