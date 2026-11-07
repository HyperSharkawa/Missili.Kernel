using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>插件公开发送能力固定真实来源与代次，任务工具的草稿另由宿主接管。</summary>
internal sealed class PluginMessageSender : IMessageSender
{
    private readonly PluginGeneration _owner;
    private readonly IMessageSender _sender;

    /// <summary>只保存宿主服务与所属代次，不向插件提供裸发送入口。</summary>
    public PluginMessageSender(PluginGeneration owner, IMessageSender sender) { _owner = owner; _sender = sender; }

    /// <summary>实际发送退出前保留代次资格，禁止普通插件伪造 Agent 或其他插件的归属。</summary>
    public async Task<BatchSendResult> SendAsync(OutgoingBatch batch, CancellationToken cancellationToken = default)
    {
        if (batch.Origin.TaskId.HasValue || batch.Origin.AgentId.HasValue || batch.Origin.TurnId.HasValue || batch.Origin.ToolInvocationId.HasValue ||
            batch.SourceToolId is not null || batch.Origin.PluginId is not null && batch.Origin.PluginId != _owner.PluginId)
            throw new ArgumentException("插件发送不能指定宿主任务或其他插件的调用归属。");
        using IHandlerLease? lease = _owner.TryAcquireCapability();
        if (lease is null) return new BatchSendResult { BatchId = batch.Id, State = BatchSendState.Cancelled, ErrorKind = "PluginUnavailable", Error = "发送插件代次不可用。" };
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.LifetimeToken);
        return await _sender.SendAsync(batch with { Origin = new OperationOrigin { PluginId = _owner.PluginId } }, linked.Token).ConfigureAwait(false);
    }
}
