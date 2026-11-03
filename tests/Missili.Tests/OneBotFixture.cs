using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;
using Missili.Kernel;
using Missili.Nexus;
using Missili.OneBot.Mock;

namespace Missili.Tests;

/// <summary>真实 ASP.NET Core、编译插件、SQLite 与独立协议 Mock 的整链路夹具。</summary>
internal sealed class OneBotFixture : IAsyncDisposable
{
    private readonly WebApplication _application;
    public string Root { get; }
    public string HostUrl { get; private set; } = "";
    public MockPeer Peer { get; } = new();
    public InfrastructureRuntime Runtime => _application.Services.GetRequiredService<InfrastructureRuntime>();
    public string Role { get; private set; } = "Universal";
    public Dictionary<string, object> Settings { get; } = new();

    /// <summary>使用生产宿主入口，仅覆盖数据目录、监听地址与日志输出。</summary>
    private OneBotFixture(TimeProvider? clock)
    {
        Root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _application = Missili.Host.Program.BuildApplication([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Infrastructure:DataDirectory"] = Root });
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            if (clock is not null) builder.Services.AddSingleton(clock);
        });
    }

    /// <summary>启动真实宿主和两端连接，配置仅含合成账号与测试 Token。</summary>
    public static async Task<OneBotFixture> CreateAsync(bool reverse, bool split, CancellationToken token,
        Action<Dictionary<string, object>>? configure = null, Action<MockPeer>? configurePeer = null, bool connect = true, TimeProvider? clock = null)
    {
        OneBotFixture fixture = new(clock);
        try
        {
            fixture.Peer.Token = "synthetic-token";
            configurePeer?.Invoke(fixture.Peer);
            await fixture._application.StartAsync(token);
            fixture.HostUrl = fixture._application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.Settings["id"] = "qq-test"; fixture.Settings["botAccountId"] = "10001";
            fixture.Settings["direction"] = reverse ? "Reverse" : "Forward"; fixture.Settings["layout"] = split ? "Split" : "Universal";
            fixture.Settings["token"] = "synthetic-token";
            fixture.Role = split ? "Event" : "Universal";
            if (!reverse)
            {
                await fixture.Peer.StartServerAsync(token: token);
                string url = fixture.Peer.BaseUrl!.Replace("http:", "ws:", StringComparison.Ordinal);
                if (split) { fixture.Settings["apiUrl"] = url + "/api"; fixture.Settings["eventUrl"] = url + "/event"; }
                else fixture.Settings["url"] = url + "/";
            }
            configure?.Invoke(fixture.Settings);
            PluginStatus loaded = await fixture.Runtime.Plugins.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Adapters", "onebot"), fixture.Configuration(), token);
            if (loaded.State != PluginState.Active) throw new InvalidOperationException("OneBot 测试插件初始化失败：" + loaded.Error);
            if (reverse && connect)
            {
                string url = fixture.HostUrl.Replace("http:", "ws:", StringComparison.Ordinal) + "/adapters/qq-test/ws";
                await fixture.Peer.ConnectAsync(url, split ? "API" : "Universal", token);
                if (split) await fixture.Peer.ConnectAsync(url, "Event", token);
            }
            if (connect) await fixture.WaitReadyAsync(token);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    /// <summary>配置只序列化共享字典，不将适配器类型加载进测试默认上下文。</summary>
    public JsonElement Configuration() => JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["instances"] = new[] { Settings } });
    /// <summary>只等待可观察 API 可用性，轮询让出执行不推测固定延迟。</summary>
    public async Task WaitReadyAsync(CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Peer.WaitConnectedAsync(Role, deadline.Token);
        while (!Runtime.Adapters.GetStatuses().Any(status => status.ApiAvailable)) await Task.Delay(1, deadline.Token);
    }
    /// <summary>按平台标识等待真实数据库出现消息，而非用固定睡眠推测入库完成。</summary>
    public async Task<MessageSnapshot> WaitMessageAsync(string platformId, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            HistoryPage page = await Runtime.History.QueryAsync(TestStore.Query(), deadline.Token);
            MessageSnapshot? message = page.Messages.FirstOrDefault(item => item.PlatformMessageId == platformId);
            if (message is not null) return message;
            await Task.Delay(1, deadline.Token);
        }
    }
    /// <summary>跳过已处理的身份查询，只等待特定实际操作。</summary>
    public async Task<MockAction> WaitActionAsync(string action, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true) { MockAction received = await Peer.NextActionAsync(deadline.Token); if (received.Action == action) return received; }
    }
    /// <summary>构造与该测试适配器身份完全一致的目标。</summary>
    public static MessageAddress Address(bool group = true) => new() { AdapterId = "qq-test", BotAccountId = "10001", PlatformId = "qq", ConversationKind = group ? "group" : "private", ConversationId = group ? "20001" : "30001" };

    /// <summary>停止生产宿主后检查所有插件回收，清理范围只限本夹具创建的目录。</summary>
    public async ValueTask DisposeAsync()
    {
        PluginManager manager = (PluginManager)Runtime.Plugins;
        await _application.StopAsync();
        await _application.DisposeAsync();
        await Peer.DisposeAsync();
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missili.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("测试目录越界。");
        WeakReference[] references = manager.GetStatuses().Select(status => manager.GetLastUnloadReference(status.Id)).Where(item => item is not null).Cast<WeakReference>().ToArray();
        for (int attempt = 0; attempt < 20 && references.Any(item => item.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        if (references.Any(item => item.IsAlive)) throw new InvalidOperationException("OneBot 测试结束后仍有加载上下文未回收。");
        // 上下文弱引用失效之后，运行时仍需完成加载器文件句柄的终结过程。
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "history.db"), ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(connection);
        Directory.Delete(Root, recursive: true);
    }
}
