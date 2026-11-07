using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Samples.ModelChat;

/// <summary>只响应明确允许会话中的 /model，不承担自动回复或 Agent 决策。</summary>
public sealed class Entry : IPlugin
{
    private IPluginContext? _context;
    private ImmutableHashSet<MessageAddress> _allowed = [];
    private readonly ConcurrentDictionary<MessageAddress, byte> _active = new();

    /// <summary>读取完整会话白名单并注册显式命令，不发起任何模型请求。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _allowed = JsonSerializer.Deserialize<ImmutableArray<MessageAddress>>(context.Configuration.GetProperty("allowedConversations"), JsonSerializerOptions.Web).ToImmutableHashSet();
        if (_allowed.Count == 0) throw new ArgumentException("模型测试插件必须明确配置允许的会话。");
        foreach (MessageAddress address in _allowed) address.Validate();
        context.Handlers.RegisterCommand("model", "模型联调", "构建当前会话上下文并完整回复一次", ["/model"], 0, ReplyAsync,
            message => _allowed.Contains(message.OriginalMessage.Address));
        return Task.CompletedTask;
    }

    /// <summary>停止依赖调用令牌；Nexus 等待在途处理真正退出。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>同一会话只执行一个测试请求，完整成功后才经过统一发送服务回复。</summary>
    private async Task ReplyAsync(MessageContext message, CancellationToken token)
    {
        MessageAddress address = message.OriginalMessage.Address;
        if (!_active.TryAdd(address, 0)) { await SendAsync(address, message.OriginalMessage.Id, "该会话的模型测试正在执行，请稍后再试。", token).ConfigureAwait(false); return; }
        try
        {
            ConversationContextSnapshot context = await _context!.ConversationContext.CaptureAsync(RemoveCommand(message), token).ConfigureAwait(false);
            (ImmutableArray<string> Models, string Instructions, bool Stream) settings = ReadSettings(context.Profile, _context.PluginId);
            ModelResult result = await _context.Models.GenerateAsync(new ModelRequest
            {
                Purpose = "reply",
                Profile = context.Profile,
                Input = ModelInput.FromConversation(context, settings.Instructions),
                Candidates = settings.Models,
                Conversation = address,
                TriggerMessageId = message.OriginalMessage.Id,
                Stream = settings.Stream
            }, cancellationToken: token).ConfigureAwait(false);
            if (result.State == ModelCallState.Cancelled) return;
            if (result.State == ModelCallState.RecordingFailed)
            { _context.Logger.LogError("模型调用 {CallId} 记录失败，本次不发送生成结果。", result.CallId); return; }
            if (result.State != ModelCallState.Succeeded || !result.Complete)
            { await SendAsync(address, message.OriginalMessage.Id, "模型调用失败，请查看调用记录。", token).ConfigureAwait(false); return; }
            if (result.Content.Any(block => block.Kind == ModelBlockKind.ToolCall))
            { await SendAsync(address, message.OriginalMessage.Id, "模型返回了工具调用；此测试入口不执行工具。", token).ConfigureAwait(false); return; }
            if (string.IsNullOrWhiteSpace(result.Text))
            { await SendAsync(address, message.OriginalMessage.Id, "模型没有返回可发送的文本。", token).ConfigureAwait(false); return; }
            await SendAsync(address, message.OriginalMessage.Id, result.Text, token).ConfigureAwait(false);
        }
        finally { _active.TryRemove(address, out byte unused); }
    }

    /// <summary>插件自己的用途配置来自会话快照，模型目录和凭据仍由宿主统一维护。</summary>
    private static (ImmutableArray<string> Models, string Instructions, bool Stream) ReadSettings(ConversationProfileSnapshot profile, string pluginId)
    {
        if (!profile.Profile.Plugins.TryGetValue(pluginId, out JsonElement settings))
            throw new ArgumentException("当前配置档缺少模型测试插件配置。");
        foreach (JsonProperty field in settings.EnumerateObject())
            if (field.Name is not ("models" or "systemPrompt" or "stream")) throw new ArgumentException("模型测试插件配置包含未知字段：" + field.Name);
        ImmutableArray<string> models = settings.GetProperty("models").EnumerateArray()
            .Select(item => item.GetString() ?? throw new ArgumentException("候选模型标识不能为空。")).ToImmutableArray();
        string prompt = settings.GetProperty("systemPrompt").GetString() ?? throw new ArgumentException("系统提示词不能为 null。");
        bool stream = !settings.TryGetProperty("stream", out JsonElement streaming) || streaming.GetBoolean();
        if (models.IsEmpty || models.Distinct(StringComparer.Ordinal).Count() != models.Length)
            throw new ArgumentException("模型测试候选必须非空且不重复。");
        foreach (string id in models)
            if (!profile.Models.TryGetValue(id, out ModelDefinition? model) ||
                profile.Providers[model.ProviderId].Protocol == ModelProtocol.AudioTranscription || stream && !model.Streaming)
                throw new ArgumentException("模型测试候选不存在或能力不匹配：" + id);
        return (models, prompt, stream);
    }

    /// <summary>仅从当前输入移除命令前缀，原始历史和其他消息段保持原义。</summary>
    private static MessageContext RemoveCommand(MessageContext context)
        => context with { CurrentContent = context.Command!.Arguments };

    /// <summary>所有成功文本和测试状态通知都保存发送意图与回执。</summary>
    private async Task SendAsync(MessageAddress address, long trigger, string text, CancellationToken token)
    {
        BatchSendResult result = await _context!.Sender.SendAsync(OutgoingBatch.Single(address, [SegmentValues.Text(text)]) with { TriggerMessageId = trigger }, token).ConfigureAwait(false);
        if (!result.Confirmed) _context.Logger.LogWarning("模型测试回复未取得已记录的成功回执。");
    }
}
