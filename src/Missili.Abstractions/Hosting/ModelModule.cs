namespace Missili.Abstractions;

/// <summary>模型模块的宿主生命周期与共享入口，具体子服务不接管插件生命周期。</summary>
public interface IModelModule : IModelService
{
    IConversationContextService Contexts { get; }
    IMediaTranscriptionService Transcriptions { get; }
    IMediaRepresentationService Representations { get; }
    IModelCallHistory Calls { get; }
    /// <summary>存储就绪后开放调用并启动有限工作循环。</summary>
    void Start();
    /// <summary>配置的插件加载后才开始扫描历史无成功转述的媒体。</summary>
    void StartBackgroundScan();
    /// <summary>归档或会话媒体关联发生变化时仅提交合并唤醒信号。</summary>
    void NotifyMediaChanged();
    /// <summary>取消并等待实际工作和诊断写入退出，存储仍须保持可用。</summary>
    Task StopAsync();
}
