using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Missili.Tests;

/// <summary>真实回环 HTTP 服务，仅提供测试创建的合成字节。</summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    public Uri BaseAddress { get; }

    /// <summary>保存已启动应用及其随机分配的回环地址。</summary>
    private TestHttpServer(WebApplication application, Uri address)
    {
        _application = application;
        BaseAddress = address;
    }

    /// <summary>启动单个测试处理器，不开放外部网卡。</summary>
    public static async Task<TestHttpServer> StartAsync(RequestDelegate handler, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication application = builder.Build();
        application.Run(handler);
        await application.StartAsync(cancellationToken);
        IServer server = application.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHttpServer(application, new Uri(address));
    }

    /// <summary>结束本测试的请求和监听，不触碰其他 HTTP 服务。</summary>
    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync(CancellationToken.None);
        await _application.DisposeAsync();
    }
}
