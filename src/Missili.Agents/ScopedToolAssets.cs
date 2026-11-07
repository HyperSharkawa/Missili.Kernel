using System.Collections.Immutable;
using Missili.Abstractions;

namespace Missili.Agents;

/// <summary>工具的归档读取绑定会话与可见性；准备阶段只能读取现有状态。</summary>
internal sealed class ScopedToolAssets : IMediaStore, IStructuredContentStore, IMediaTranscriptionService, IConversationAssetAccess
{
    private readonly AgentToolContext _context;
    private readonly AgentAuthorization _authorization;
    private readonly string _toolId;
    private readonly bool _actions;
    private readonly bool _refresh;

    /// <summary>刷新资格必须已经成为冻结动作的附加权限，不能在工具执行时偷偷升级。</summary>
    public ScopedToolAssets(AgentToolContext context, AgentAuthorization authorization, string toolId, bool actions, bool refresh)
    { _context = context; _authorization = authorization; _toolId = toolId; _actions = actions; _refresh = refresh; }

    /// <summary>调用者提供的会话和可见性不能扩大宿主固定的范围。</summary>
    public Task<bool> CanReadAsync(MessageAddress conversation, ConversationAssetKind kind, Guid id, MessageVisibilityPolicy visibility, CancellationToken cancellationToken = default)
        => conversation != _context.Conversation ? Task.FromResult(false) : _context.Assets.CanReadAsync(conversation, kind, id, _context.Profile.Profile.Visibility, cancellationToken);

    /// <summary>状态也按来源关系隔离，不能用 GUID 探测其他会话归档。</summary>
    public async Task<MediaStatus?> GetStatusAsync(Guid id, CancellationToken cancellationToken = default)
        => await AllowedAsync(ConversationAssetKind.Media, id, cancellationToken).ConfigureAwait(false)
            ? await _context.Media.GetStatusAsync(id, cancellationToken).ConfigureAwait(false) : null;

    /// <summary>等待原件后再次核对撤回状态，失去读取资格时先释放已打开的流。</summary>
    public async Task<MediaReadResult> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(ConversationAssetKind.Media, id, cancellationToken).ConfigureAwait(false);
        MediaReadResult result = await _context.Media.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
        if (await AllowedAsync(ConversationAssetKind.Media, id, cancellationToken).ConfigureAwait(false)) return result;
        await result.DisposeAsync().ConfigureAwait(false); throw new KeyNotFoundException("当前获准范围内没有该原件。");
    }

    /// <summary>只交付归档正文及局部失败，内部恢复检查点不进入模型上下文。</summary>
    public async Task<StructuredContentSnapshot?> GetContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(ConversationAssetKind.Content, id, cancellationToken).ConfigureAwait(false)) return null;
        StructuredContentSnapshot? result = await _context.Contents.GetContentAsync(id, cancellationToken).ConfigureAwait(false);
        return result is null ? null : result with { Checkpoint = null };
    }

    /// <summary>等待已有归档不扩大授权；等待期间原引用撤回时不再返回已缓存原文。</summary>
    public async Task<StructuredContentSnapshot> WaitContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAsync(ConversationAssetKind.Content, id, cancellationToken).ConfigureAwait(false);
        StructuredContentSnapshot result = await _context.Contents.WaitContentAsync(id, cancellationToken).ConfigureAwait(false);
        await RequireAsync(ConversationAssetKind.Content, id, cancellationToken).ConfigureAwait(false);
        return result with { Checkpoint = null };
    }

    /// <summary>模型读取只能展开已关联来源，不能通过任意平台身份补取其他会话内容。</summary>
    public Task<Guid> RequestContentAsync(ContentSource source, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("模型工具只能读取已有会话引用，不接受自由平台内容查询。");

    /// <summary>成功缓存保留模型与调用来源，提示词属于诊断信息而非媒体正文。</summary>
    public async Task<TranscriptionSnapshot?> GetAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(ConversationAssetKind.Media, mediaId, cancellationToken).ConfigureAwait(false)) return null;
        TranscriptionSnapshot? result = await _context.Transcriptions.GetAsync(mediaId, cancellationToken).ConfigureAwait(false);
        return Visible(result);
    }

    /// <summary>显式转述使用固定会话配置；刷新再检查初始与当前权限，不被传入的配置替换。</summary>
    public async Task<MediaTranscriptionResult> EnsureAsync(Guid mediaId, ConversationProfileSnapshot profile, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!_actions) throw new InvalidOperationException("工具准备阶段不能启动转述。");
        if (refresh && (!_refresh || await _authorization.CheckAsync(_context.Conversation, _context.Origin, _toolId, [AgentPermissions.RefreshTranscription], cancellationToken).ConfigureAwait(false) != AgentActionAccess.Allowed))
            throw new InvalidOperationException("本次动作没有获准刷新转述。");
        await RequireAsync(ConversationAssetKind.Media, mediaId, cancellationToken).ConfigureAwait(false);
        MediaTranscriptionResult result = await _context.Transcriptions.EnsureAsync(mediaId, _context.Profile, refresh, cancellationToken).ConfigureAwait(false);
        await RequireAsync(ConversationAssetKind.Media, mediaId, cancellationToken).ConfigureAwait(false);
        return result with { Cached = Visible(result.Cached) };
    }

    /// <summary>源关系校验始终使用实际固定会话，不接受插件自报可见性。</summary>
    private Task<bool> AllowedAsync(ConversationAssetKind kind, Guid id, CancellationToken token)
        => _context.Assets.CanReadAsync(_context.Conversation, kind, id, _context.Profile.Profile.Visibility, token);

    /// <summary>不存在与范围外采用同一错误，避免泄露其他会话资源。</summary>
    private async Task RequireAsync(ConversationAssetKind kind, Guid id, CancellationToken token)
    {
        if (!await AllowedAsync(kind, id, token).ConfigureAwait(false)) throw new KeyNotFoundException("当前获准范围内没有该归档。");
    }

    /// <summary>普通模型读取不展开转述系统提示词。</summary>
    private static TranscriptionSnapshot? Visible(TranscriptionSnapshot? result)
        => result is null ? null : result with { Sources = result.Sources.Select(source => source with { Prompt = null }).ToImmutableArray() };
}
