using System.Text;
using System.Text.Json;
using Fixture.PrivateDependency;
using Missili.Abstractions;

namespace Fixture.Plugin;

/// <summary>仅供加载器与归档验收的编译驱动，不连接任何真实账号。</summary>
public sealed class Adapter : IAdapterDriver
{
    /// <summary>返回合成在线状态，身份由目录登记再次约束。</summary>
    public AdapterStatus GetStatus() => new() { AdapterId = "fixture-adapter", BotAccountId = "bot", PlatformId = "synthetic", Connected = true, ApiAvailable = true, EventsAvailable = true, IdentityVerified = true };
    /// <summary>返回一次确定回执，测试并不模拟协议编码。</summary>
    public Task<TransportResult> SendAsync(long messageId, OutgoingMessage message, CancellationToken cancellationToken) =>
        Task.FromResult(new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "fixture-" + messageId });
    /// <summary>验证不同 DLL 实际执行版本及加载代次取消。</summary>
    public async Task<PlatformOperationResult> InvokeAsync(string action, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (action == "hold") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new PlatformOperationResult { State = PlatformOperationState.Succeeded, Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["version"] = VersionInfo.Read() }) };
    }
    /// <summary>生命周期夹具只验证动态拒绝路由，实际 WebSocket 由 OneBot 测试覆盖。</summary>
    public Task AcceptAsync(IWebSocketRequest request, CancellationToken cancellationToken) => request.RejectAsync(403, cancellationToken);
    /// <summary>返回定义在可回收程序集内的流，证明打开流会保留代次资格。</summary>
    public Task<MediaSourceRead> ResolveMediaAsync(MediaSource source, CancellationToken cancellationToken) =>
        Task.FromResult(new MediaSourceRead { Content = new PluginStream(), Representation = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["format"] = "synthetic", ["original"] = true }) });
    /// <summary>提供有效的单采样 PCM WAV，用于验证派生格式读取和代次归属。</summary>
    public Task<MediaSourceRead> ResolveRepresentationAsync(MediaSource source, string format, CancellationToken cancellationToken)
    {
        if (format != "wav") throw new MediaAcquisitionException("测试转换只提供 WAV。");
        MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(38); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(2); writer.Write((short)0);
        }
        stream.Position = 0;
        return Task.FromResult(new MediaSourceRead { Content = stream });
    }
    /// <summary>重复提交相同节点位置，测试保存恢复不会重复创建媒体。</summary>
    public async Task ExpandContentAsync(StructuredContentSnapshot current, Func<ContentExpansion, CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        IncomingContentNode node = new()
        {
            Path = "0",
            SenderId = "user",
            Segments = [new IncomingSegment { Kind = SegmentTypes.Text, Text = "归档正文" },
                new IncomingSegment { Kind = SegmentTypes.Image, Media = [new IncomingMedia { Kind = MediaKind.Image,
                    Source = new MediaSource { Kind = "fixture", AdapterId = "fixture-adapter", BotAccountId = "bot", Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["file"] = "test" }) } }] }]
        };
        await save(new ContentExpansion { State = ContentState.Expanding, Nodes = [node] }, cancellationToken);
        await save(new ContentExpansion { State = ContentState.Complete, Nodes = [node] }, cancellationToken);
    }

    /// <summary>插件专有流类型只用于检验宿主是否正确持有和释放调用资格。</summary>
    private sealed class PluginStream : MemoryStream
    {
        /// <summary>提供小型合成原件，不读写磁盘。</summary>
        public PluginStream() : base(Encoding.UTF8.GetBytes("fixture-media"), writable: false) { }
    }
}
