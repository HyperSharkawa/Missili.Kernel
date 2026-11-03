using System.Threading.Channels;

namespace Missili.Kernel;

/// <summary>持久化业务队列的合并唤醒信号，避免发送返回等待后续监听。</summary>
public sealed class DispatchSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(1);
    /// <summary>通知已有持久化记录待分发；重复信号可以合并。</summary>
    public void Pulse() => _channel.Writer.TryWrite(0);
    /// <summary>等待变化并清除已经合并的信号。</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        while (_channel.Reader.TryRead(out _)) { }
    }
}
