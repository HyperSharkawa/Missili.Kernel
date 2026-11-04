using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Missili.Abstractions;
using Missili.Host;
using Missili.Kernel;
using Missili.Nexus;

namespace Missili.Tests;

/// <summary>使用生产组装函数的隔离运行时，传输是唯一的外部业务替身。</summary>
internal sealed class RuntimeFixture : IAsyncDisposable
{
    public string Root { get; }
    public InfrastructureOptions Options { get; }
    public RecordingTransport Transport { get; }
    public InfrastructureRuntime Runtime { get; }

    /// <summary>创建独立数据目录及真实框架，不加载任何真实账号配置。</summary>
    private RuntimeFixture(Action<InfrastructureOptions>? configure, TimeProvider? clock, bool enableModels, ModelOptions? modelOptions)
    {
        Root = Path.Combine(Path.GetTempPath(), "Missili.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Options = new InfrastructureOptions { DataDirectory = Root };
        configure?.Invoke(Options);
        Transport = new RecordingTransport();
        ModelOptions models = (modelOptions ?? new ModelOptions()) with { Enabled = enableModels, ConfigurationDirectory = Path.Combine(Root, "model-config") };
        Runtime = InfrastructureFactory.Create(Options, NullLoggerFactory.Instance, [Transport], clock, models);
    }

    /// <summary>执行真实宿主初始化与数据库迁移。</summary>
    public static async Task<RuntimeFixture> CreateAsync(CancellationToken token, Action<InfrastructureOptions>? configure = null, TimeProvider? clock = null,
        ModelConfiguration? models = null, ModelOptions? modelOptions = null)
    {
        RuntimeFixture fixture = new(configure, clock, models is not null, modelOptions);
        if (models is not null) await fixture.WriteModelsAsync(models, token);
        await fixture.Runtime.StartAsync(token);
        return fixture;
    }

    /// <summary>写入测试独占的实际配置文件，模型模块仍通过生产文件读取路径初始化。</summary>
    public async Task WriteModelsAsync(ModelConfiguration models, CancellationToken token)
        => await WriteModelConfigurationAsync(Root, models, token);

    /// <summary>同一配置写入器供内核夹具和真实 Web 宿主夹具复用。</summary>
    internal static async Task WriteModelConfigurationAsync(string root, ModelConfiguration models, CancellationToken token)
    {
        string directory = Path.Combine(root, "model-config");
        Directory.CreateDirectory(Path.Combine(directory, "profiles"));
        JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        await File.WriteAllBytesAsync(Path.Combine(directory, "catalog.json"), JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        { ["providers"] = models.Providers, ["models"] = models.Models }, json), token);
        await File.WriteAllBytesAsync(Path.Combine(directory, "routes.json"), JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        { ["defaultProfileId"] = models.DefaultProfileId, ["routes"] = models.Routes }, json), token);
        for (int index = 0; index < models.Profiles.Length; index++)
            await File.WriteAllBytesAsync(Path.Combine(directory, "profiles", index + ".json"), JsonSerializer.SerializeToUtf8Bytes(models.Profiles[index], json), token);
    }

    /// <summary>复制实际构建产物并生成测试清单，不把插件程序集加载到默认上下文。</summary>
    public async Task<string> PackageAsync(string id, string version, CancellationToken token, int? concurrency = null)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", version);
        string destination = Path.Combine(Root, "packages", id, version);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        PluginManifest manifest = new()
        {
            SchemaVersion = 1,
            ContractVersion = 3,
            Id = id,
            Name = id,
            Version = version,
            EntryAssembly = "Fixture.Plugin.dll",
            EntryType = "Fixture.Plugin.Entry",
            MaxConcurrency = concurrency
        };
        await File.WriteAllBytesAsync(Path.Combine(destination, "plugin.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), token);
        return destination;
    }

    /// <summary>创建普通值配置，不让测试代码引用插件私有类型。</summary>
    public static JsonElement Configuration(string mode = "normal", string? resourceMarker = null)
    {
        Dictionary<string, string> configuration = new() { ["mode"] = mode };
        if (resourceMarker is not null) configuration["resourceMarker"] = resourceMarker;
        return JsonSerializer.SerializeToElement(configuration);
    }

    /// <summary>按数据库状态等待业务完成；轮询延迟只用于让出执行，不用来推断顺序。</summary>
    public async Task<MessageSnapshot> FinishedAsync(long id, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            MessageSnapshot snapshot = (await Runtime.History.GetAsync(id, deadline.Token))!;
            if (snapshot.Processing is ProcessingState.Finished or ProcessingState.Interrupted) return snapshot;
            await Task.Delay(10, deadline.Token);
        }
    }

    /// <summary>真实释放所有服务，然后只清理本测试拥有的目录。</summary>
    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        // 已完成取消任务的续体可能仍在退出栈帧，先证明弱引用消失再清理 DLL。
        // 此操作仅用于实际卸载验收，不进入生产路径，也不跳过失败。
        PluginManager manager = (PluginManager)Runtime.Plugins;
        WeakReference[] references = manager.GetStatuses().Select(status => manager.GetLastUnloadReference(status.Id))
            .Where(reference => reference is not null).Cast<WeakReference>().ToArray();
        for (int attempt = 0; attempt < 20 && references.Any(reference => reference.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Yield(); }
        if (references.Any(reference => reference.IsAlive)) throw new InvalidOperationException("测试结束后插件加载上下文仍被引用，不能清理运行目录。");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "history.db"), ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(connection);
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Missili.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.Ordinal)) throw new InvalidOperationException("测试目录越界。");
        Directory.Delete(Root, true);
    }
}

/// <summary>只记录内存中的合成传输请求，不连接任何平台。</summary>
internal sealed class RecordingTransport : IMessageTransport
{
    private readonly Channel<OutgoingMessage> _messages = Channel.CreateBounded<OutgoingMessage>(1024);
    public string AdapterId => "test";
    public ConcurrentQueue<OutgoingMessage> Requests { get; } = new();
    public Func<long, OutgoingMessage, CancellationToken, Task<TransportResult>>? OnSend { get; set; }

    /// <summary>记录一次调用，并由测试指定回执、等待或失败。</summary>
    public async Task<TransportResult> SendAsync(long messageId, OutgoingMessage message, CancellationToken cancellationToken)
    {
        Requests.Enqueue(message);
        await _messages.Writer.WriteAsync(message, cancellationToken);
        if (OnSend is not null) return await OnSend(messageId, message, cancellationToken);
        return new TransportResult { State = DeliveryState.Confirmed, PlatformMessageId = "synthetic-" + messageId };
    }

    /// <summary>等待真实进入传输边界的指定消息，超时只用作失败上界。</summary>
    public async Task<OutgoingMessage> WaitForAsync(Func<OutgoingMessage, bool> predicate, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            OutgoingMessage message = await _messages.Reader.ReadAsync(deadline.Token);
            if (predicate(message)) return message;
        }
    }
}
