using System.Collections.Immutable;
using System.Net.WebSockets;
using Missili.Abstractions;
using Missili.Kernel;

namespace Missili.Host;

/// <summary>宿主固定入口仅路由注册实例，不引用任何具体协议插件。</summary>
internal sealed class AdapterWebSocketEndpoint : IWebSocketRequest
{
    private readonly HttpContext _http;
    public bool IsWebSocketRequest => _http.WebSockets.IsWebSocketRequest;
    public ImmutableDictionary<string, string> Headers { get; }

    /// <summary>请求信息只活到连接处理结束，不进入长期状态或日志。</summary>
    private AdapterWebSocketEndpoint(HttpContext http)
    {
        _http = http;
        Headers = http.Request.Headers.ToImmutableDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>等待完整连接生命周期后才返回，遵守 ASP.NET Core 升级请求约束。
    /// 依据：https://learn.microsoft.com/aspnet/core/fundamentals/websockets 。</summary>
    public static async Task HandleAsync(HttpContext context, string adapterId, InfrastructureRuntime runtime)
    {
        if (runtime.Adapters is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        await runtime.Adapters.AcceptAsync(adapterId, new AdapterWebSocketEndpoint(context), context.RequestAborted);
    }

    /// <summary>按实例配置启用协议保活，业务心跳关闭不影响此机制。</summary>
    public async Task<WebSocket> AcceptAsync(TimeSpan keepAliveInterval, TimeSpan keepAliveTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        { KeepAliveInterval = keepAliveInterval, KeepAliveTimeout = keepAliveTimeout });
    }

    /// <summary>仅设置状态码，拒绝响应不回显身份或凭据。</summary>
    public Task RejectAsync(int statusCode, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); _http.Response.StatusCode = statusCode; return Task.CompletedTask; }
}
