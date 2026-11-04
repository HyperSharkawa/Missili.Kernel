using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;

namespace Missili.Providers;

/// <summary>聚合 SDK 已解析的协议事件，保留完整内容块和不完整工具参数。</summary>
internal sealed class ProtocolAccumulator
{
    private readonly ModelProtocol _protocol;
    private readonly SortedDictionary<int, JsonObject> _blocks = [];
    private readonly Dictionary<int, StringBuilder> _arguments = [];
    private readonly SortedDictionary<(int Output, int Content), StringBuilder> _responseTexts = [];
    private readonly HashSet<int> _finishedBlocks = [];
    private readonly StringBuilder _text = new();
    private JsonNode? _continuation;
    private ModelUsage _usage = new();
    private string? _finish;
    private bool _complete;

    /// <summary>一次尝试创建一个聚合器，候选间不共享输出。</summary>
    public ProtocolAccumulator(ModelProtocol protocol) => _protocol = protocol;

    /// <summary>接收完整非流式响应，不把协议错误当作空文字成功。</summary>
    public void Complete(JsonElement response)
    {
        if (_protocol == ModelProtocol.ChatCompletions)
        {
            JsonElement choice = response.GetProperty("choices")[0];
            _continuation = JsonNode.Parse(choice.GetProperty("message").GetRawText());
            _finish = String(choice, "finish_reason");
            _complete = _finish is not null;
        }
        else if (_protocol == ModelProtocol.Responses)
        {
            _continuation = JsonNode.Parse(response.GetProperty("output").GetRawText());
            _finish = String(response, "status");
            _complete = _finish == "completed";
        }
        else
        {
            _continuation = JsonNode.Parse(response.GetProperty("content").GetRawText());
            _finish = String(response, "stop_reason");
            _complete = _finish is not null;
        }
        Usage(response);
    }

    /// <summary>按协议聚合一个事件并返回其可消费文字增量。</summary>
    public string? Add(JsonElement data)
    {
        string? delta = null;
        if (_protocol == ModelProtocol.ChatCompletions)
        {
            Usage(data);
            if (!data.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) return null;
            JsonElement choice = choices[0];
            _finish = String(choice, "finish_reason") ?? _finish;
            if (!choice.TryGetProperty("delta", out JsonElement change)) return null;
            delta = String(change, "content") ?? String(change, "refusal");
            if (change.TryGetProperty("tool_calls", out JsonElement calls))
            {
                foreach (JsonElement tool in calls.EnumerateArray())
                {
                    int index = tool.GetProperty("index").GetInt32();
                    if (!_blocks.TryGetValue(index, out JsonObject? block))
                    {
                        block = new JsonObject { ["type"] = "function", ["function"] = new JsonObject() };
                        _blocks.Add(index, block); _arguments.Add(index, new StringBuilder());
                    }
                    if (String(tool, "id") is string id) block["id"] = id;
                    if (tool.TryGetProperty("function", out JsonElement function))
                    {
                        if (String(function, "name") is string name) block["function"]!["name"] = name;
                        _arguments[index].Append(String(function, "arguments"));
                    }
                }
            }
        }
        else if (_protocol == ModelProtocol.Responses)
        {
            string type = data.GetProperty("type").GetString()!;
            if (type == "response.output_item.added") _blocks[data.GetProperty("output_index").GetInt32()] = JsonNode.Parse(data.GetProperty("item").GetRawText())!.AsObject();
            if (type is "response.output_text.delta" or "response.refusal.delta")
            {
                delta = String(data, "delta");
                int output = data.GetProperty("output_index").GetInt32(), content = data.GetProperty("content_index").GetInt32();
                if (!_responseTexts.TryGetValue((output, content), out StringBuilder? text)) _responseTexts[(output, content)] = text = new StringBuilder();
                text.Append(delta);
                if (!_blocks.ContainsKey(output)) _blocks[output] = new JsonObject { ["type"] = "message", ["id"] = String(data, "item_id"), ["role"] = "assistant", ["status"] = "in_progress" };
            }
            if (type == "response.function_call_arguments.delta")
            {
                int output = data.GetProperty("output_index").GetInt32();
                if (!_arguments.TryGetValue(output, out StringBuilder? arguments)) _arguments[output] = arguments = new StringBuilder();
                arguments.Append(String(data, "delta"));
            }
            if (type == "response.output_item.done")
            { int output = data.GetProperty("output_index").GetInt32(); _blocks[output] = JsonNode.Parse(data.GetProperty("item").GetRawText())!.AsObject(); _finishedBlocks.Add(output); }
            if (type is "response.completed" or "response.incomplete" or "response.failed") Complete(data.GetProperty("response"));
            if (type == "error") throw new InvalidDataException("模型流返回协议错误。");
        }
        else
        {
            string type = data.GetProperty("type").GetString()!;
            if (type == "message_start") Usage(data.GetProperty("message"));
            if (type == "content_block_start")
            {
                int index = data.GetProperty("index").GetInt32();
                _blocks[index] = JsonNode.Parse(data.GetProperty("content_block").GetRawText())!.AsObject();
                _arguments[index] = new StringBuilder();
            }
            if (type == "content_block_delta")
            {
                int index = data.GetProperty("index").GetInt32();
                JsonObject block = _blocks[index];
                JsonElement change = data.GetProperty("delta");
                string kind = change.GetProperty("type").GetString()!;
                if (kind == "text_delta") { delta = String(change, "text"); block["text"] = (block["text"]?.GetValue<string>() ?? "") + delta; }
                else if (kind == "input_json_delta") _arguments[index].Append(String(change, "partial_json"));
                else if (kind == "thinking_delta") block["thinking"] = (block["thinking"]?.GetValue<string>() ?? "") + String(change, "thinking");
                else if (kind == "signature_delta") block["signature"] = (block["signature"]?.GetValue<string>() ?? "") + String(change, "signature");
            }
            if (type == "content_block_stop")
            {
                int index = data.GetProperty("index").GetInt32();
                if (_arguments[index].Length > 0) _blocks[index]["input"] = JsonNode.Parse(_arguments[index].ToString());
                _finishedBlocks.Add(index);
            }
            if (type == "message_delta") { _finish = String(data.GetProperty("delta"), "stop_reason"); Usage(data); }
            if (type == "message_stop") _complete = _finish is not null;
            if (type == "error") throw new InvalidDataException("模型流返回协议错误。");
        }
        _text.Append(delta);
        return delta;
    }

    /// <summary>Chat SDK 的枚举结束后仍要求有效结束原因；截断输出保持不完整。</summary>
    public void EndStream()
    {
        if (_protocol == ModelProtocol.ChatCompletions)
        {
            _complete = _finish is not null;
            JsonArray calls = [];
            foreach (KeyValuePair<int, JsonObject> pair in _blocks)
            { pair.Value["function"]!["arguments"] = _arguments[pair.Key].ToString(); calls.Add(pair.Value.DeepClone()); }
            JsonObject message = new() { ["role"] = "assistant", ["content"] = _text.ToString() };
            if (calls.Count > 0) message["tool_calls"] = calls;
            _continuation = message;
        }
        else if (_continuation is null)
        {
            JsonArray items = [];
            foreach (KeyValuePair<int, JsonObject> pair in _blocks)
            {
                JsonObject item = pair.Value.DeepClone().AsObject();
                if (_protocol == ModelProtocol.Responses && !_finishedBlocks.Contains(pair.Key))
                {
                    if (item["type"]?.GetValue<string>() == "message")
                    {
                        JsonArray parts = [];
                        foreach (KeyValuePair<(int Output, int Content), StringBuilder> text in _responseTexts.Where(entry => entry.Key.Output == pair.Key))
                            parts.Add(new JsonObject { ["type"] = "output_text", ["text"] = text.Value.ToString(), ["annotations"] = new JsonArray() });
                        item["content"] = parts;
                    }
                    else if (_arguments.TryGetValue(pair.Key, out StringBuilder? arguments)) item["arguments"] = arguments.ToString();
                }
                items.Add(item);
            }
            _continuation = items;
        }
    }

    /// <summary>输出纯数据；不完整工具参数只作部分结果，不提供可执行输入。</summary>
    public ModelProtocolResult Result(ModelCallState? state = null, string? error = null, string? errorKind = null)
    {
        try { return BuildResult(state, error, errorKind); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // 最终载荷自身也可能损坏，错误返回不能再次解析同一损坏对象而抛出第二次异常。
            ImmutableArray<ModelBlock>.Builder partial = ImmutableArray.CreateBuilder<ModelBlock>();
            if (_text.Length > 0) partial.Add(ModelBlock.TextBlock(_text.ToString()) with { Complete = false });
            if (_continuation is not null) partial.Add(new ModelBlock { Kind = ModelBlockKind.Native, Data = JsonSerializer.SerializeToElement(_continuation), Complete = false });
            return new ModelProtocolResult
            {
                State = state == ModelCallState.Cancelled ? state.Value : ModelCallState.Failed,
                Content = partial.ToImmutable(),
                Usage = _usage,
                FinishReason = _finish,
                Error = "模型最终内容结构无效。",
                ErrorKind = "Protocol"
            };
        }
    }

    /// <summary>转换已聚合的原生结果，严格解析已完成的工具参数，失败交由外层形成可读诊断。</summary>
    private ModelProtocolResult BuildResult(ModelCallState? state, string? error, string? errorKind)
    {
        if (_continuation is null) EndStream();
        bool complete = _complete && (state is null or ModelCallState.Succeeded);
        ImmutableArray<ModelBlock>.Builder content = ImmutableArray.CreateBuilder<ModelBlock>();
        if (_continuation is not null)
        {
            if (_protocol == ModelProtocol.ChatCompletions)
            {
                string? text = _continuation["content"]?.GetValue<string>() ?? _continuation["refusal"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) content.Add(ModelBlock.TextBlock(text));
                if (_continuation["tool_calls"] is JsonArray calls)
                    foreach (JsonNode? call in calls) AddTool(content, call!["id"]?.GetValue<string>(), call["function"]!["name"]?.GetValue<string>(), call["function"]!["arguments"]?.GetValue<string>(), complete);
            }
            else
            {
                foreach (JsonNode? node in _continuation.AsArray())
                {
                    string? type = node?["type"]?.GetValue<string>();
                    if (type == "message" && node!["content"] is JsonArray parts)
                    {
                        foreach (JsonNode? part in parts)
                        {
                            if (part?["type"]?.GetValue<string>() is "output_text" or "text") content.Add(ModelBlock.TextBlock(part["text"]!.GetValue<string>()));
                            else if (part?["type"]?.GetValue<string>() == "refusal") content.Add(ModelBlock.TextBlock(part["refusal"]!.GetValue<string>()));
                        }
                    }
                    else if (type == "text") content.Add(ModelBlock.TextBlock(node!["text"]!.GetValue<string>()));
                    else if (type == "function_call") AddTool(content, node!["call_id"]?.GetValue<string>(), node["name"]?.GetValue<string>(), node["arguments"]?.GetValue<string>(), complete);
                    else if (type == "tool_use") AddTool(content, node!["id"]?.GetValue<string>(), node["name"]?.GetValue<string>(),
                        complete ? node["input"]?.ToJsonString() : PartialArguments(node["id"]?.GetValue<string>()) ?? node["input"]?.ToJsonString(), complete);
                    else if (node is not null) content.Add(new ModelBlock { Kind = ModelBlockKind.Native, Data = JsonSerializer.SerializeToElement(node), Complete = _complete });
                }
            }
        }
        if (content.All(block => block.Kind != ModelBlockKind.Text) && _text.Length > 0) content.Insert(0, ModelBlock.TextBlock(_text.ToString()) with { Complete = false });
        return new ModelProtocolResult
        {
            State = state ?? (complete ? ModelCallState.Succeeded : ModelCallState.Failed),
            Content = content.Select(block => complete ? block : block with { Complete = false }).ToImmutableArray(),
            Complete = complete,
            Usage = _usage,
            FinishReason = _finish,
            Continuation = complete && _continuation is not null ? JsonSerializer.SerializeToElement(_continuation) : null,
            Error = error ?? (complete ? null : "模型响应未完整结束。"),
            ErrorKind = errorKind ?? (complete ? null : "Incomplete")
        };
    }

    /// <summary>中断的工具参数保留原始片段，不将初始空对象伪装成已完成输入。</summary>
    private string? PartialArguments(string? id)
    {
        foreach (KeyValuePair<int, JsonObject> item in _blocks)
            if (item.Value["id"]?.GetValue<string>() == id && _arguments.TryGetValue(item.Key, out StringBuilder? value) && value.Length > 0) return value.ToString();
        return null;
    }

    /// <summary>只有完整且有效的 JSON 参数才能作为工具调用数据返回。</summary>
    private static void AddTool(ImmutableArray<ModelBlock>.Builder output, string? id, string? name, string? arguments, bool complete)
    {
        JsonElement? value = null;
        if (complete && arguments is not null) { using JsonDocument document = JsonDocument.Parse(arguments); value = document.RootElement.Clone(); }
        output.Add(new ModelBlock { Kind = ModelBlockKind.ToolCall, ToolCallId = id, Name = name, Data = value, Text = value is null ? arguments : null, Complete = complete && value.HasValue });
    }

    /// <summary>不同协议的可选用量字段只在实际存在时更新。</summary>
    private void Usage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object) return;
        long? cached = Number(usage, "cache_read_input_tokens");
        if (usage.TryGetProperty("prompt_tokens_details", out JsonElement prompt) && prompt.ValueKind == JsonValueKind.Object) cached = Number(prompt, "cached_tokens");
        if (usage.TryGetProperty("input_tokens_details", out JsonElement input) && input.ValueKind == JsonValueKind.Object) cached = Number(input, "cached_tokens");
        _usage = new ModelUsage
        {
            InputTokens = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens") ?? _usage.InputTokens,
            OutputTokens = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens") ?? _usage.OutputTokens,
            CachedInputTokens = cached ?? _usage.CachedInputTokens,
            CacheWriteTokens = Number(usage, "cache_creation_input_tokens") ?? _usage.CacheWriteTokens
        };
    }
    /// <summary>读取可空字符串，不把协议空值变成正文。</summary>
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    /// <summary>读取实际提供的用量，不估计未知值。</summary>
    private static long? Number(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Number ? field.GetInt64() : null;
}
