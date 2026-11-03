using System.Collections.Immutable;
using Fixture.PrivateDependency;
using Missili.Abstractions;

namespace Fixture.Plugin;

/// <summary>仅用于隔离验收的真实编译插件，所有发送均走测试传输。</summary>
public sealed class Entry : IPlugin
{
    private IPluginContext? _context;
    private string _mode = "normal";

    /// <summary>按测试配置注册真实处理器、后台任务和资源。</summary>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _mode = context.Configuration.TryGetProperty("mode", out System.Text.Json.JsonElement mode) ? mode.GetString()! : "normal";
        if (_mode.StartsWith("adapter", StringComparison.Ordinal))
        {
            if (_mode == "adapter-with-handler") context.Handlers.RegisterCommand("adapter-command", "适配器命令", "校验注册冲突时不发布部分命令", "/adapter", 0, CommandAsync);
            context.Adapters.Register(new AdapterRegistration { AdapterId = "fixture-adapter", BotAccountId = "bot", PlatformId = "synthetic", Driver = new Adapter(), ReverseWebSocket = true });
            _ = context.Tasks.RunAfterActivation(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
            if (_mode == "adapter-init-fail") throw new InvalidOperationException("适配器注册后的合成失败。");
            return Task.CompletedTask;
        }
        context.Handlers.RegisterInterceptor("prefix", "测试拦截器", "验证异常与修改边界", 0, InterceptAsync);
        context.Handlers.RegisterListener(MessageHooks.Received, "reply", "测试回复", "只调用测试传输", 0, ReplyAsync);
        context.Handlers.RegisterCommand("history", "测试命令", "验证先接管再执行", "/history", 0, CommandAsync, MatchCommand);
        if (_mode == "after-sent") context.Handlers.RegisterListener(MessageHooks.Sent, "after", "发送后通知", "验证单并发发送不会自锁", 0, AfterSentAsync);
        if (_mode is "event" or "event-throw") context.Handlers.RegisterEventListener("event", "事件监听", "验证先保存、后通知与异常隔离", 0, OnEventAsync);
        if (_mode is "background" or "init-fail") _ = context.Tasks.Run(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        if (context.Configuration.TryGetProperty("resourceMarker", out System.Text.Json.JsonElement marker))
            _ = context.Tasks.Track(new MarkerResource(marker.GetString()!));
        if (_mode == "init-fail") throw new InvalidOperationException("合成的初始化失败。");
        return Task.CompletedTask;
    }

    /// <summary>本夹具的后台工作通过登记令牌停止，无额外真实服务。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>从公开历史读取已提交事件后才发送测试标记，异常模式验证其他插件仍被调用。</summary>
    private async Task OnEventAsync(PlatformEventSnapshot platformEvent, CancellationToken token)
    {
        if (_mode == "event-throw") throw new InvalidOperationException("合成的事件监听失败。");
        PlatformEventSnapshot stored = await _context!.Events.GetEventAsync(platformEvent.Id, token) ?? throw new InvalidOperationException("事件尚未落库。");
        IncomingPlatformEvent input = stored.Event;
        await _context.Sender.SendAsync(new OutgoingMessage
        {
            Address = new MessageAddress
            {
                AdapterId = input.AdapterId,
                BotAccountId = input.BotAccountId,
                PlatformId = input.PlatformId,
                ConversationKind = input.ConversationKind!,
                ConversationId = input.ConversationId!
            },
            Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = "event:" + input.Type }]
        }, token);
    }

    /// <summary>根据模式模拟显式拦截、异常或成功替换；不会修改原始快照。</summary>
    private async Task<HandlerResult> InterceptAsync(MessageContext message, CancellationToken token)
    {
        if (_mode == "throw-interceptor") throw new InvalidOperationException("合成的拦截器失败。");
        if (_mode == "intercept") return new HandlerResult { Outcome = HandlerOutcome.Handled };
        if (_mode is "wait" or "ignore-cancellation")
        {
            await SendAsync(message, "hold", _mode == "ignore-cancellation" ? CancellationToken.None : token);
            if (_mode == "wait") token.ThrowIfCancellationRequested();
        }
        if (_mode is "normal" or "after-sent" or "wait" or "ignore-cancellation")
        {
            ImmutableArray<MessageSegment> content = message.CurrentContent.Select(segment => segment.Kind == SegmentTypes.Text && segment.ParseState == SegmentParseState.Parsed && !segment.Text!.StartsWith('/')
                ? segment with { Text = VersionInfo.Read() + ">" + segment.Text } : segment).ToImmutableArray();
            return new HandlerResult { Outcome = HandlerOutcome.Replace, Content = content };
        }
        return new HandlerResult();
    }

    /// <summary>媒体模式只在当前调用需要文件时等待，普通文本直接回复。</summary>
    private async Task ReplyAsync(MessageContext message, CancellationToken token)
    {
        if (_mode == "throw-listener") throw new InvalidOperationException("合成的监听失败。");
        MessageSegment? media = message.CurrentContent.FirstOrDefault(segment => !segment.MediaIds.IsEmpty);
        if (_mode == "media" && media is not null)
        {
            await using MediaReadResult result = await _context!.Media.OpenReadAsync(media.MediaIds[0], token);
            await SendAsync(message, result.Content is null ? "media-error" : "media-ready", token);
            return;
        }
        string text = string.Join(" ", message.CurrentContent.Where(segment => segment.Kind == SegmentTypes.Text && segment.ParseState == SegmentParseState.Parsed).Select(segment => segment.Text));
        await SendAsync(message, VersionInfo.Read() + ":" + text, token);
    }

    /// <summary>允许独立测试匹配失败与已接管后的执行失败。</summary>
    private bool MatchCommand(MessageContext message)
    {
        if (_mode == "command-match-fail") throw new InvalidOperationException("合成的匹配失败。");
        return true;
    }

    /// <summary>执行合成命令；执行失败时不得再次触发普通回复。</summary>
    private Task CommandAsync(MessageContext message, CancellationToken token)
    {
        if (_mode == "command-fail") throw new InvalidOperationException("合成的命令失败。");
        return SendAsync(message, "command:" + VersionInfo.Read(), token);
    }

    /// <summary>仅为普通回复产生一次后续发送，后续发送本身不会再次回复。</summary>
    private Task AfterSentAsync(MessageContext message, CancellationToken token)
    {
        string text = message.OriginalMessage.Segments[0].Text!;
        return text.StartsWith("after:", StringComparison.Ordinal) ? Task.CompletedTask : SendAsync(message, "after:" + text, token);
    }

    /// <summary>统一经过宿主发送服务，夹具没有自己的网络平台客户端。</summary>
    private async Task SendAsync(MessageContext message, string text, CancellationToken token)
    {
        await _context!.Sender.SendAsync(new OutgoingMessage
        { Address = message.OriginalMessage.Address, Segments = [new MessageSegment { Kind = SegmentTypes.Text, Text = text }] }, token);
    }

    /// <summary>释放时写入测试指定的临时标记，用于证明资源实际释放。</summary>
    private sealed class MarkerResource : IAsyncDisposable
    {
        private readonly string _path;
        /// <summary>保存由测试创建的临时目标。</summary>
        public MarkerResource(string path) => _path = path;
        /// <summary>实际执行异步文件写入，不通过模拟状态报告释放成功。</summary>
        public async ValueTask DisposeAsync() => await File.WriteAllTextAsync(_path, "released");
    }
}
