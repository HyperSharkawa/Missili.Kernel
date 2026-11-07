using System.Text;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>共享 JSON 记录的有限展开，不接收插件私有类型或向宿主登记反射元数据。</summary>
public static class ToolResultPages
{
    /// <summary>按 UTF-16 偏移返回 JSON 文本片段，以实际编码字节控制模型结果容量。</summary>
    public static ToolExecutionResult Json(JsonElement record, int offset, int length, int maximumBytes)
    {
        if (offset < 0 || length is < 1 or > 65536) throw new ArgumentException("记录分页参数无效。");
        string json = record.GetRawText();
        if (offset > json.Length || offset > 0 && offset < json.Length && char.IsLowSurrogate(json[offset]))
            return new ToolExecutionResult { State = ToolCallState.Rejected, Error = "offset 超出记录或位于代理对内部。" };
        int count = Math.Min(length, json.Length - offset);
        while (true)
        {
            if (count > 0 && char.IsHighSurrogate(json[offset + count - 1])) count--;
            JsonElement data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["format"] = "json_text",
                ["offset"] = offset,
                ["total_length"] = json.Length,
                ["next_offset"] = offset + count < json.Length ? offset + count : null,
                ["content"] = json.Substring(offset, count),
                ["complete"] = offset == 0 && count == json.Length
            });
            if (Encoding.UTF8.GetByteCount(data.GetRawText()) <= maximumBytes - 1024) return new ToolExecutionResult { State = ToolCallState.Succeeded, Data = data };
            if (count == 0) return new ToolExecutionResult { State = ToolCallState.Failed, Error = "模型结果预算不足以容纳分页信息。", ErrorKind = "OutputLimit" };
            count /= 2;
        }
    }
}
