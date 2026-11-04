using System.Collections.Immutable;
using System.Text.Json;
using Missili.Abstractions;
using SixLabors.ImageSharp;

namespace Missili.Models;

/// <summary>把固定聊天历史投影为带来源的会话资料，真实模型轮次与群成员消息保持分离。</summary>
public sealed class ConversationContextService : IConversationContextService
{
    private readonly IModelStore _store;
    private readonly IInfrastructureStore _history;
    private readonly IConversationProfileService _profiles;
    private readonly IMediaStore _media;
    private readonly IMediaTranscriptionService _transcriptions;
    private readonly IMediaRepresentationService _representations;
    private readonly IStructuredContentStore _contents;

    /// <summary>只依赖共享契约，不直接调用数据库上下文或平台网络客户端。</summary>
    public ConversationContextService(IModelStore store, IInfrastructureStore history, IConversationProfileService profiles, IMediaStore media,
        IMediaTranscriptionService transcriptions, IMediaRepresentationService representations, IStructuredContentStore contents)
    { _store = store; _history = history; _profiles = profiles; _media = media; _transcriptions = transcriptions; _representations = representations; _contents = contents; }

    /// <summary>历史和配置各捕获一次，处理器修改只应用到本次触发消息。</summary>
    public async Task<ConversationContextSnapshot> CaptureAsync(MessageContext message, CancellationToken cancellationToken = default)
    {
        ConversationProfileSnapshot profile = _profiles.Resolve(message.OriginalMessage.Address);
        ConversationWindow window = await _store.CaptureWindowAsync(message.OriginalMessage.Address, profile.Profile.MaximumMessages, profile.Profile.DiscardMessages, cancellationToken).ConfigureAwait(false);
        window = window with
        {
            Messages = window.Messages.Select(item => item.Id == message.OriginalMessage.Id
            ? item with { Segments = message.CurrentContent.Select(segment => segment.Freeze()).ToImmutableArray() } : item).ToImmutableArray()
        };
        // 积压处理中窗口可能已淘汰较早的触发消息；当前显式请求仍必须提供一次，不回退持久淘汰边界。
        if (!window.Messages.Any(item => item.Id == message.OriginalMessage.Id))
            window = window with { Messages = window.Messages.Add(message.OriginalMessage with { Segments = message.CurrentContent.Select(segment => segment.Freeze()).ToImmutableArray() }) };
        return new ConversationContextSnapshot { Profile = profile, Address = message.OriginalMessage.Address, TriggerMessageId = message.OriginalMessage.Id, Window = window };
    }

    /// <summary>候选能力只决定媒体表示，不改变固定的历史记录集合。</summary>
    public async Task<ImmutableArray<ModelMessage>> ProjectAsync(ConversationContextSnapshot context, ModelDefinition model, CancellationToken cancellationToken = default)
    {
        ImmutableArray<ModelMessage>.Builder messages = ImmutableArray.CreateBuilder<ModelMessage>();
        messages.Add(new ModelMessage { Role = "system", Content = [ModelBlock.TextBlock(context.Profile.Profile.SystemPrompt)] });
        foreach (MessageSnapshot message in context.Window.Messages)
        {
            ImmutableArray<ModelBlock>.Builder content = ImmutableArray.CreateBuilder<ModelBlock>();
            string source = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["messageId"] = message.Id,
                ["platformMessageId"] = message.PlatformMessageId,
                ["senderId"] = message.SenderId,
                ["receivedAt"] = message.ReceivedAt,
                ["occurredAt"] = message.OccurredAt,
                ["direction"] = message.Direction.ToString()
            });
            content.Add(ModelBlock.TextBlock("[聊天资料来源] " + source + "\n"));
            if (message.Recalled || message.Deleted)
            {
                string marker = message.Deleted ? "[此消息已删除]" : "[此消息已撤回]";
                if (context.Profile.Profile.Visibility != ModelVisibilityPolicy.Original) content.Add(ModelBlock.TextBlock(marker));
                if (context.Profile.Profile.Visibility == ModelVisibilityPolicy.Placeholder)
                { messages.Add(new ModelMessage { Role = "user", Content = content.ToImmutable() }); continue; }
            }
            await AppendSegmentsAsync(content, message.Segments, context, model, new HashSet<Guid>(), cancellationToken).ConfigureAwait(false);
            // 历史中的 Bot 发言也属于聊天资料，不伪造为当前模型的原生输出或签名轮次。
            messages.Add(new ModelMessage { Role = "user", Content = content.ToImmutable() });
        }
        return messages.ToImmutable();
    }

    /// <summary>按原始顺序保留段含义；展示占位只存在于模型投影。</summary>
    private async Task AppendSegmentsAsync(ImmutableArray<ModelBlock>.Builder output, ImmutableArray<MessageSegment> segments,
        ConversationContextSnapshot context, ModelDefinition model, HashSet<Guid> path, CancellationToken token)
    {
        foreach (MessageSegment segment in segments)
        {
            bool localReply = false;
            token.ThrowIfCancellationRequested();
            if (segment.ParseState != SegmentParseState.Parsed)
            {
                output.Add(ModelBlock.TextBlock($"[{(segment.ParseState == SegmentParseState.Invalid ? "解析失败" : "未解释")}的 {segment.Kind} 消息段] {segment.Payload?.GetRawText()}"));
                continue;
            }
            if (segment.Kind == SegmentTypes.Text) { output.Add(ModelBlock.TextBlock(segment.Text!)); continue; }
            if (segment.Kind == SegmentTypes.Video)
            { output.Add(ModelBlock.TextBlock(Field(segment, "title") is string title ? "[视频，标题：" + title + "]" : "[视频，尚未解析内容]")); continue; }
            if (segment.Kind == SegmentTypes.Mention) { output.Add(ModelBlock.TextBlock("[提及用户：" + segment.MentionTarget() + "]")); continue; }
            if (segment.Kind == SegmentTypes.Reply)
            {
                string target = segment.ReplyTarget();
                output.Add(ModelBlock.TextBlock("[引用消息：" + target + "]"));
                // 引用优先读取已存历史，投影不额外发起 get_msg 或把补取内容当作新群消息。
                MessageSnapshot? local = await _history.FindPlatformMessageAsync(context.Address, target, null, token).ConfigureAwait(false);
                if (local is not null && local.Id <= context.Window.HighWatermark)
                {
                    localReply = true;
                    if ((local.Recalled || local.Deleted) && context.Profile.Profile.Visibility == ModelVisibilityPolicy.Placeholder)
                        output.Add(ModelBlock.TextBlock("[引用原文已撤回或删除]"));
                    else
                    {
                        output.Add(ModelBlock.TextBlock("[引用发言者：" + local.SenderId + "]"));
                        if ((local.Recalled || local.Deleted) && context.Profile.Profile.Visibility == ModelVisibilityPolicy.MarkedOriginal)
                            output.Add(ModelBlock.TextBlock("[引用原文已撤回或删除]"));
                        await AppendSegmentsAsync(output, local.Segments.Where(item => item.Kind != SegmentTypes.Reply).ToImmutableArray(), context, model, path, token).ConfigureAwait(false);
                    }
                }
                else if (segment.ContentId is null) output.Add(ModelBlock.TextBlock("[当前没有可用的引用原文]"));
            }
            if (!localReply && segment.ContentId is Guid contentId)
            {
                if (!path.Add(contentId)) { output.Add(ModelBlock.TextBlock("[结构化内容出现循环引用]")); continue; }
                StructuredContentSnapshot? content = await _contents.GetContentAsync(contentId, token).ConfigureAwait(false);
                if (content is null) output.Add(ModelBlock.TextBlock("[转发或引用内容尚不可用]"));
                else
                {
                    output.Add(ModelBlock.TextBlock("[结构化内容开始：" + content.Source.PlatformId + "]"));
                    await AppendNodesAsync(output, content.Nodes, context, model, path, token).ConfigureAwait(false);
                    foreach (ContentIssue issue in content.Issues) output.Add(ModelBlock.TextBlock($"[位置 {issue.Path}：{issue.Description}]"));
                    if (content.State != ContentState.Complete) output.Add(ModelBlock.TextBlock("[内容尚未完整展开，状态：" + content.State + "]"));
                    output.Add(ModelBlock.TextBlock("[结构化内容结束]"));
                }
                path.Remove(contentId);
            }
            if (segment.MediaIds.Length > 0)
            {
                foreach (Guid id in segment.MediaIds) await AppendMediaAsync(output, id, context.Profile, model, token).ConfigureAwait(false);
                continue;
            }
            if (segment.Kind is not (SegmentTypes.Reply or SegmentTypes.Forward))
                output.Add(ModelBlock.TextBlock("[" + segment.Kind + "] " + segment.Payload?.GetRawText()));
        }
    }

    /// <summary>保留节点发言者、时间和层次，所有节点仍属于同一条历史消息。</summary>
    private async Task AppendNodesAsync(ImmutableArray<ModelBlock>.Builder output, ImmutableArray<ContentNode> nodes,
        ConversationContextSnapshot context, ModelDefinition model, HashSet<Guid> path, CancellationToken token)
    {
        foreach (ContentNode node in nodes)
        {
            output.Add(ModelBlock.TextBlock("[节点来源] " + JsonSerializer.Serialize(new Dictionary<string, object?>
            { ["path"] = node.Path, ["senderId"] = node.SenderId, ["senderName"] = node.SenderName, ["occurredAt"] = node.OccurredAt })));
            await AppendSegmentsAsync(output, node.Segments, context, model, path, token).ConfigureAwait(false);
            await AppendNodesAsync(output, node.Children, context, model, path, token).ConfigureAwait(false);
        }
    }

    /// <summary>缓存、原件和明确占位按配置选择，单个媒体失败不丢弃其他聊天资料。</summary>
    private async Task AppendMediaAsync(ImmutableArray<ModelBlock>.Builder output, Guid mediaId, ConversationProfileSnapshot profile, ModelDefinition model, CancellationToken token)
    {
        MediaStatus? status = await _media.GetStatusAsync(mediaId, token).ConfigureAwait(false);
        if (status is null) { output.Add(ModelBlock.TextBlock("[媒体引用不存在]")); return; }
        if (status.Kind is not (MediaKind.Image or MediaKind.Audio)) { output.Add(ModelBlock.TextBlock("[" + (status.Kind == MediaKind.Video ? "视频" : "文件") + "]")); return; }
        TranscriptionSnapshot? cache = await _transcriptions.GetAsync(mediaId, token).ConfigureAwait(false);
        ModelMediaPolicy policy = status.Kind == MediaKind.Image ? profile.Profile.Images : profile.Profile.Audio;
        bool capable = status.Kind == MediaKind.Image ? model.Images : model.Audio;
        if (cache?.Text is not null && (policy != ModelMediaPolicy.OriginalPreferred || !capable || status.State == MediaState.Cleaned))
        { output.Add(ModelBlock.TextBlock("[媒体转述] " + cache.Text)); return; }
        if (status.State == MediaState.Cleaned) { output.Add(ModelBlock.TextBlock("[原件已清理、暂无转述]")); return; }
        if (policy == ModelMediaPolicy.TextOnly || policy == ModelMediaPolicy.TranscriptionPreferred && !capable)
        {
            MediaTranscriptionResult result = await _transcriptions.EnsureAsync(mediaId, profile, cancellationToken: token).ConfigureAwait(false);
            output.Add(ModelBlock.TextBlock(result.Succeeded ? "[媒体转述] " + result.Cached!.Text : "[媒体转述失败，内容暂不可用]"));
            return;
        }
        if (!capable) { output.Add(ModelBlock.TextBlock("[该候选不能读取此媒体类型]")); return; }
        try
        {
            if (status.Kind == MediaKind.Audio)
            {
                MediaRepresentation audio = await _representations.PrepareAudioAsync(mediaId, model, token).ConfigureAwait(false);
                output.Add(new ModelBlock { Kind = ModelBlockKind.Audio, MediaId = audio.MediaId, MimeType = audio.MimeType });
            }
            else
            {
                await using MediaReadResult read = await _media.OpenReadAsync(mediaId, token).ConfigureAwait(false);
                if (read.Content is null) { output.Add(ModelBlock.TextBlock("[图片原件不可读取]")); return; }
                ImageInfo info = await Image.IdentifyAsync(read.Content, token).ConfigureAwait(false);
                output.Add(new ModelBlock { Kind = ModelBlockKind.Image, MediaId = mediaId, MimeType = info.Metadata.DecodedImageFormat!.DefaultMimeType });
            }
        }
        catch (Exception exception) when (exception is ModelInputException or IOException or UnknownImageFormatException or InvalidImageContentException or AdapterUnavailableException or NotSupportedException)
        { output.Add(ModelBlock.TextBlock("[媒体准备失败，内容暂不可用]")); }
    }

    /// <summary>只取实际存在的标题字段，不依据地址猜测视频内容。</summary>
    private static string? Field(MessageSegment segment, string name) => segment.Payload is JsonElement data && data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
