using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>一个代理独占的有序上下文；全部平台聊天共用窗口，协议轮次独立保留。</summary>
internal sealed class AgentConversation
{
    private readonly MessageAddress _address;
    private readonly IModelStore _windows;
    private readonly IMessageHistory _history;
    private readonly bool _includeChat;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<long, ImmutableArray<MessageSegment>> _overrides = [];

    /// <summary>子代理仅接收父代理明确传入的资料，不复制会话聊天或主代理人设。</summary>
    public AgentConversation(MessageAddress address, IModelStore windows, IMessageHistory history, bool includeChat)
    { _address = address; _windows = windows; _history = history; _includeChat = includeChat; }

    /// <summary>冻结后的内容只覆盖仍在窗口中的源消息，唤醒通知不创建长期保护条目。</summary>
    public void Accept(AgentWakeInput input) => _overrides[input.MessageId] = input.Content;

    /// <summary>真实模型输出、工具结果和树内通信保持实际顺序。</summary>
    public void Append(ModelMessage message) => _entries.Add(new Entry { Turn = message.Freeze() });

    /// <summary>重读有效窗口并原位更新来源状态，淘汰消息及其处理覆盖一同移除，不保证触发消息至少出现一次。</summary>
    public async Task<ModelInput> CaptureAsync(ConversationProfileSnapshot profile, string instructions, AgentOutputMode outputMode, CancellationToken token)
    {
        ConversationWindow window = _includeChat
            ? await _windows.CaptureWindowAsync(_address, profile.Profile.MaximumMessages, profile.Profile.DiscardMessages, token).ConfigureAwait(false)
            : new ConversationWindow { Messages = [], HighWatermark = 0, DiscardedThrough = 0, Revision = 0 };
        Dictionary<long, MessageSnapshot> available = window.Messages.ToDictionary(message => message.Id);
        foreach (KeyValuePair<long, ImmutableArray<MessageSegment>> overlay in _overrides.ToArray())
        {
            long? canonical = (await _history.GetAsync(overlay.Key, token).ConfigureAwait(false))?.Id;
            if (canonical is not long id || !available.TryGetValue(id, out MessageSnapshot? source))
            { _overrides.Remove(overlay.Key); continue; }
            available[id] = source with { Segments = overlay.Value };
            if (id != overlay.Key) { _overrides.Remove(overlay.Key); _overrides[id] = overlay.Value; }
        }
        HashSet<long> represented = [];
        foreach (Entry entry in _entries.Where(entry => entry.Turn is null))
        {
            ImmutableArray<MessageSnapshot>.Builder messages = ImmutableArray.CreateBuilder<MessageSnapshot>();
            foreach (MessageSnapshot previous in entry.Messages)
            {
                long id = previous.Id;
                if (!available.ContainsKey(id)) id = (await _history.GetAsync(id, token).ConfigureAwait(false))?.Id ?? id;
                if (available.TryGetValue(id, out MessageSnapshot? current) && represented.Add(id)) messages.Add(current);
            }
            entry.Messages = messages.ToImmutable();
        }
        _entries.RemoveAll(entry => entry.Turn is null && entry.Messages.IsEmpty);
        ImmutableArray<MessageSnapshot> added = window.Messages.Where(message => represented.Add(message.Id)).Select(message => available[message.Id]).ToImmutableArray();
        if (!added.IsEmpty) _entries.Add(new Entry { Messages = added });

        // 运行事实随本次请求冻结，不向用户提示词追加人格或服从要求。
        JsonElement facts = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["kind"] = "host_runtime_context", ["conversation"] = _address, ["is_main_agent"] = _includeChat,
            ["output_mode"] = outputMode == AgentOutputMode.Direct ? "direct" : "tools_only"
        });
        ImmutableArray<ModelInputItem>.Builder items = ImmutableArray.CreateBuilder<ModelInputItem>();
        items.Add(new ModelTurnInput { Message = new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("[宿主运行事实] " + facts.GetRawText())] } });
        foreach (Entry entry in _entries)
        {
            if (entry.Turn is ModelMessage turn) items.Add(new ModelTurnInput { Message = turn });
            else items.Add(new ConversationSliceInput
            {
                Conversation = new ConversationContextSnapshot { Address = _address, Profile = profile, Window = window with { Messages = entry.Messages } }
            });
        }
        return new ModelInput { Instructions = instructions, Items = items.ToImmutable() };
    }

    /// <summary>续接约束只来自真实协议轮次，普通聊天和审计数据不锁定候选。</summary>
    public bool CanUse(ModelDefinition model, ModelProviderDefinition provider) => _entries.Where(entry => entry.Turn?.Continuation is not null)
        .All(entry => entry.Turn!.Continuation is ModelContinuation continuation && continuation.Protocol == provider.Protocol && continuation.ProviderId == provider.Id &&
            continuation.ModelId == model.Id && continuation.Endpoint == provider.BaseUrl && continuation.ModelName == model.Name);

    /// <summary>聊天切片只保存当前窗口可见项，工具与模型轮次不受平台聊天数量淘汰。</summary>
    private sealed class Entry
    {
        public ModelMessage? Turn { get; init; }
        public ImmutableArray<MessageSnapshot> Messages { get; set; } = [];
    }
}
