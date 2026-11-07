using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;

namespace Missili.Providers;

/// <summary>通用内容到三个协议的显式映射；协议专有数据保持原始归属。</summary>
internal sealed class ProtocolPayload
{
    public Dictionary<string, string> MediaValues { get; } = new(StringComparer.Ordinal);
    private readonly IMediaStore _media;
    /// <summary>媒体读取通过宿主原件契约，不接触宿主路径。</summary>
    public ProtocolPayload(IMediaStore media) => _media = media;

    /// <summary>生成请求载荷；SDK 仍负责正式序列化、发送和流解析。</summary>
    public async Task<JsonObject> BuildAsync(ModelAttemptRequest attempt, CancellationToken token)
    {
        ModelRequest request = attempt.Request;
        ModelProtocol protocol = attempt.Provider.Protocol;
        if (attempt.Messages.Sum(message => message.Content.Count(block => block.Kind == ModelBlockKind.Image)) > attempt.Model.MaxImages)
            throw new ModelInputException("请求图片数量超过候选配置上限。");
        JsonObject body = new() { ["model"] = attempt.Model.Name };
        string limitName = protocol == ModelProtocol.Responses ? "max_output_tokens" : protocol == ModelProtocol.Anthropic ? "max_tokens" : "max_completion_tokens";
        body[limitName] = request.Profile.Profile.Execution.MaxOutputTokens;
        if (protocol == ModelProtocol.Responses) { body["store"] = false; body["stream"] = request.Stream; }
        if (protocol == ModelProtocol.ChatCompletions && request.Stream)
        { body["stream"] = true; body["stream_options"] = new JsonObject { ["include_usage"] = true }; }
        if (protocol == ModelProtocol.Anthropic) body["stream"] = request.Stream;
        JsonArray messages = [];
        List<string> system = [];
        for (int index = 0; index < attempt.Messages.Length; index++)
        {
            ModelMessage message = attempt.Messages[index];
            if (message.Role is not ("system" or "user" or "assistant" or "tool")) throw new ModelInputException("模型消息角色无效。");
            // 原生结果位于它实际所属的轮次，多个工具结果和多轮续接都不猜测插入位置。
            if (message.Continuation is not null)
            { AddContinuation(messages, message.Continuation, protocol); continue; }
            if (protocol == ModelProtocol.Anthropic && message.Role == "system")
            {
                if (message.Content.Any(block => block.Kind != ModelBlockKind.Text)) throw new ModelInputException("系统提示只能包含文本。");
                system.Add(string.Concat(message.Content.Select(block => block.Text)));
                continue;
            }
            JsonArray content = [];
            JsonArray calls = [];
            foreach (ModelBlock block in message.Content)
            {
                if (block.Kind == ModelBlockKind.Native) throw new ModelInputException("原生输出必须通过带来源的续接数据传递。");
                if (block.Kind is ModelBlockKind.ToolCall or ModelBlockKind.ToolResult)
                {
                    if (!attempt.Model.Tools) throw new ModelInputException("候选未声明工具能力。");
                    if (!block.Complete) throw new ModelInputException("不能续接未完成的工具参数。");
                    if (protocol == ModelProtocol.Responses && content.Count > 0)
                    { messages.Add(new JsonObject { ["type"] = "message", ["role"] = message.Role, ["content"] = content }); content = []; }
                    AddTool(protocol, block, content, calls, messages);
                    continue;
                }
                if (block.Kind == ModelBlockKind.Text)
                {
                    content.Add(new JsonObject { ["type"] = protocol == ModelProtocol.Responses ? (message.Role == "assistant" ? "output_text" : "input_text") : "text", ["text"] = block.Text ?? "" });
                    continue;
                }
                bool image = block.Kind == ModelBlockKind.Image;
                if (image && !attempt.Model.Images || !image && !attempt.Model.Audio) throw new ModelInputException("候选不支持当前媒体类型。");
                if (!image && protocol != ModelProtocol.ChatCompletions) throw new ModelInputException("当前协议未提供音频对话编码。");
                Guid mediaId = block.MediaId ?? throw new ModelInputException("媒体块缺少归档引用。");
                string mime = block.MimeType ?? throw new ModelInputException("媒体块缺少明确格式。");
                await using MediaReadResult read = await _media.OpenReadAsync(mediaId, token).ConfigureAwait(false);
                if (read.Content is null) throw new ModelInputException(read.Error ?? "媒体原件不可用。");
                if (read.Status.Length > attempt.Model.MaxRequestBytes) throw new ModelInputException("媒体超过模型请求体上限。");
                using MemoryStream bytes = new();
                await read.Content.CopyToAsync(bytes, token).ConfigureAwait(false);
                string encoded = Convert.ToBase64String(bytes.GetBuffer(), 0, checked((int)bytes.Length));
                string reference = "media:" + mediaId;
                MediaValues[encoded] = reference;
                string url = "data:" + mime + ";base64," + encoded;
                MediaValues[url] = reference;
                if (protocol == ModelProtocol.Anthropic)
                    content.Add(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = encoded } });
                else if (protocol == ModelProtocol.Responses)
                    content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
                else if (image)
                    content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                else
                    content.Add(new JsonObject { ["type"] = "input_audio", ["input_audio"] = new JsonObject { ["data"] = encoded, ["format"] = mime == "audio/wav" ? "wav" : mime == "audio/mpeg" ? "mp3" : throw new ModelInputException("对话音频只支持 WAV 或 MP3。") } });
            }
            if (content.Count == 0 && calls.Count == 0) continue;
            JsonObject item = new() { ["role"] = message.Role == "tool" && protocol == ModelProtocol.Anthropic ? "user" : message.Role, ["content"] = content };
            // SDK 按 type 选择消息类型；缺失时再次序列化会成为 unknown。
            // https://developers.openai.com/api/reference/resources/responses/methods/create
            if (protocol == ModelProtocol.Responses) item["type"] = "message";
            if (calls.Count > 0) item["tool_calls"] = calls;
            messages.Add(item);
        }
        if (protocol == ModelProtocol.Anthropic && system.Count > 0) body["system"] = string.Join("\n", system);
        body[protocol == ModelProtocol.Responses ? "input" : "messages"] = messages;
        if (request.Tools.Length > 0)
        {
            if (!attempt.Model.Tools) throw new ModelInputException("候选未声明工具能力。");
            JsonArray definitions = [];
            foreach (ModelTool tool in request.Tools)
            {
                JsonObject function = new()
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    [protocol == ModelProtocol.Anthropic ? "input_schema" : "parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
                };
                if (protocol == ModelProtocol.ChatCompletions) definitions.Add(new JsonObject { ["type"] = "function", ["function"] = function });
                else { if (protocol == ModelProtocol.Responses) function["type"] = "function"; definitions.Add(function); }
            }
            body["tools"] = definitions;
        }
        if (attempt.Model.Parameters is JsonElement parameters)
        {
            foreach (JsonProperty property in parameters.EnumerateObject())
            {
                if (body.ContainsKey(property.Name) || property.Name is "stream" or "api_key" or "authorization" or "previous_response_id" or "conversation")
                    throw new ModelInputException("协议扩展参数不能覆盖框架控制字段：" + property.Name);
                body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        return body;
    }

    /// <summary>完整保留同协议输出项或内容块，尤其不能丢弃推理签名。</summary>
    private static void AddContinuation(JsonArray messages, ModelContinuation continuation, ModelProtocol protocol)
    {
        if (continuation.Protocol != protocol) throw new ModelInputException("续接协议不匹配。");
        if (protocol == ModelProtocol.Responses)
        { foreach (JsonElement item in continuation.Items.EnumerateArray()) messages.Add(JsonNode.Parse(item.GetRawText())); }
        else if (protocol == ModelProtocol.Anthropic)
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(continuation.Items.GetRawText()) });
        else messages.Add(JsonNode.Parse(continuation.Items.GetRawText()));
    }

    /// <summary>工具参数和结果按协议位置编码，调用数据不转换为普通聊天文字。</summary>
    private static void AddTool(ModelProtocol protocol, ModelBlock block, JsonArray content, JsonArray calls, JsonArray messages)
    {
        bool call = block.Kind == ModelBlockKind.ToolCall;
        string id = block.ToolCallId ?? throw new ModelInputException("工具块缺少调用标识。");
        if (protocol == ModelProtocol.Responses)
            messages.Add(call ? new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = block.Name, ["arguments"] = block.Data?.GetRawText() }
                : new JsonObject { ["type"] = "function_call_output", ["call_id"] = id, ["output"] = block.Text ?? block.Data?.GetRawText() ?? "" });
        else if (protocol == ModelProtocol.Anthropic)
            content.Add(call ? new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = block.Name, ["input"] = JsonNode.Parse(block.Data?.GetRawText() ?? "{}") }
                : new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = block.Text ?? block.Data?.GetRawText() ?? "" });
        else if (call)
            calls.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = block.Name, ["arguments"] = block.Data?.GetRawText() ?? "{}" } });
        else messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = block.Text ?? block.Data?.GetRawText() ?? "" });
    }
}
