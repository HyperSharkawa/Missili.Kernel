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
        context.Handlers.RegisterCommand("model", "模型联调", "构建当前会话上下文并完整回复一次", "/model", 0, ReplyAsync,
            message => _allowed.Contains(message.OriginalMessage.Address));
        return Task.CompletedTask;
    }

    /// <summary>停止依赖调用令牌；Nexus 等待在途处理真正退出。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>同一会话只执行一个测试请求，完整成功后才经过统一发送服务回复。</summary>
    private async Task ReplyAsync(MessageContext message, CancellationToken token)
    {
        MessageAddress address = message.OriginalMessage.Address;
        if (!_active.TryAdd(address, 0)) { await SendAsync(address, "该会话的模型测试正在执行，请稍后再试。", token).ConfigureAwait(false); return; }
        try
        {
            ConversationContextSnapshot context = await _context!.ConversationContext.CaptureAsync(RemoveCommand(message), token).ConfigureAwait(false);
            ModelResult result = await _context.Models.GenerateAsync(new ModelRequest
            {
                Purpose = "reply",
                Profile = context.Profile,
                Context = context,
                Candidates = context.Profile.Profile.ReplyModels,
                Conversation = address,
                TriggerMessageId = message.OriginalMessage.Id,
                Messages = [],
                Stream = context.Profile.Profile.StreamReplies
            }, cancellationToken: token).ConfigureAwait(false);
            if (result.State == ModelCallState.Cancelled) return;
            if (result.State == ModelCallState.RecordingFailed)
            { _context.Logger.LogError("模型调用 {CallId} 记录失败，本次不发送生成结果。", result.CallId); return; }
            if (result.State != ModelCallState.Succeeded || !result.Complete)
            { await SendAsync(address, "模型调用失败，请查看调用记录。", token).ConfigureAwait(false); return; }
            if (result.Content.Any(block => block.Kind == ModelBlockKind.ToolCall))
            { await SendAsync(address, "模型返回了工具调用；此测试入口不执行工具。", token).ConfigureAwait(false); return; }
            if (string.IsNullOrWhiteSpace(result.Text))
            { await SendAsync(address, "模型没有返回可发送的文本。", token).ConfigureAwait(false); return; }
            await SendAsync(address, result.Text, token).ConfigureAwait(false);
        }
        finally { _active.TryRemove(address, out byte unused); }
    }

    /// <summary>仅从当前输入移除命令前缀，原始历史和其他消息段保持原义。</summary>
    private static MessageContext RemoveCommand(MessageContext context)
    {
        ImmutableArray<MessageSegment>.Builder segments = context.CurrentContent.ToBuilder();
        for (int index = 0; index < segments.Count; index++)
        {
            if (segments[index].Kind != SegmentTypes.Text || segments[index].ParseState != SegmentParseState.Parsed) continue;
            string text = segments[index].Text!.TrimStart();
            if (text.StartsWith("/model", StringComparison.Ordinal)) segments[index] = segments[index] with { Text = text[6..].TrimStart() };
            break;
        }
        return context with { CurrentContent = segments.ToImmutable() };
    }

    /// <summary>所有成功文本和测试状态通知都保存发送意图与回执。</summary>
    private async Task SendAsync(MessageAddress address, string text, CancellationToken token)
    {
        SendResult result = await _context!.Sender.SendAsync(new OutgoingMessage { Address = address, Segments = [SegmentValues.Text(text)] }, token).ConfigureAwait(false);
        if (result.State != DeliveryState.Confirmed || !result.Recorded) _context.Logger.LogWarning("模型测试回复未取得已记录的成功回执。");
    }
}
