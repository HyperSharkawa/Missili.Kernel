using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Missili.Abstractions;

namespace Missili.Nexus;

/// <summary>编译插件清单，版本展示与契约兼容性分开判断。</summary>
public sealed record PluginManifest
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string EntryAssembly { get; init; }
    public required string EntryType { get; init; }
    public required int ContractVersion { get; init; }
    public int? MaxConcurrency { get; init; }

    /// <summary>检查当前明确支持的包契约，拒绝非法标识与并发限制。</summary>
    public void Validate(int maximumConcurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(EntryType);
        if (SchemaVersion != 1 || ContractVersion != 2) throw new InvalidDataException("不支持的插件清单或契约版本。");
        if (!char.IsAsciiLetterOrDigit(Id[0]) || Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new InvalidDataException("插件标识只能包含 ASCII 字母、数字、点、短横线和下划线，且必须以字母或数字开头。");
        if (MaxConcurrency.HasValue && (MaxConcurrency < 1 || MaxConcurrency > maximumConcurrency))
            throw new InvalidDataException("插件并发限制超出宿主允许范围。");
        if (string.IsNullOrWhiteSpace(EntryAssembly) || !EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件入口必须是包内 DLL。");
    }
}

/// <summary>固定清单采用源生成，动态插件实现仍使用正常 JIT 加载。</summary>
[JsonSerializable(typeof(PluginManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class PluginJsonContext : JsonSerializerContext { }

/// <summary>本地包安装与运行代次复制，不提供远程下载或源码编译。</summary>
internal sealed class PluginPackages
{
    private readonly string _root;
    private readonly int _maximumConcurrency;

    /// <summary>将插件文件限制在宿主数据根目录中。</summary>
    public PluginPackages(string dataDirectory, int maximumConcurrency)
    { _root = Path.GetFullPath(Path.Combine(dataDirectory, "plugins")); _maximumConcurrency = maximumConcurrency; }

    /// <summary>读取清单与入口文件，不执行任何插件代码。</summary>
    public async Task<PluginManifest> ReadAsync(string directory, CancellationToken token)
    {
        string root = Path.GetFullPath(directory);
        await using FileStream input = File.OpenRead(Within(root, "plugin.json"));
        PluginManifest manifest = await JsonSerializer.DeserializeAsync(input, PluginJsonContext.Default.PluginManifest, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("插件清单为空。");
        manifest.Validate(_maximumConcurrency);
        string entry = Within(root, manifest.EntryAssembly);
        if (!File.Exists(entry) || !File.Exists(Path.ChangeExtension(entry, ".deps.json")))
            throw new InvalidDataException("插件缺少入口 DLL 或对应的 .deps.json。");
        return manifest;
    }

    /// <summary>解包并校验后移动到独立包目录，错误时只清理本次创建的暂存。</summary>
    public async Task<string> InstallAsync(string zipPath, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        string staging = Within(_root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("插件包不能包含符号链接。");
                string target = Within(staging, entry.FullName.Replace('\\', '/'));
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using Stream source = entry.Open();
                await using FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            }
            PluginManifest manifest = await ReadAsync(staging, token).ConfigureAwait(false);
            string destinationRoot = Within(_root, "packages/" + manifest.Id + "/" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
            Directory.Move(staging, destinationRoot);
            return destinationRoot;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(Within(_root, Path.GetRelativePath(_root, staging)), true);
        }
    }

    /// <summary>为一次加载复制独立文件，重载不覆盖正在使用的 DLL。</summary>
    public string CreateRuntime(string directory, string pluginId, Guid generation, CancellationToken token)
    {
        string destination = Within(_root, "runtime/" + pluginId + "/" + generation.ToString("N"));
        CopyDirectory(Path.GetFullPath(directory), destination, token);
        return destination;
    }

    /// <summary>复制运行文件并拒绝链接，确保入口目录仍是已检查的普通文件树。</summary>
    private static void CopyDirectory(string source, string destination, CancellationToken token)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件目录不能是链接。");
        Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("插件文件不能是链接。");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry, target, token);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    /// <summary>校验相对路径在指定根目录内；所有解包和清理路径均经过此边界。</summary>
    public static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("插件包路径无效。");
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(prefix, relative));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(prefix, comparison)) throw new InvalidDataException("插件包路径越界。");
        return full;
    }
}

/// <summary>每次加载使用独立可回收上下文，共享契约保持宿主类型身份。
/// 依据：https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support 。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly Assembly[] Shared =
    [typeof(IPlugin).Assembly, typeof(ILogger).Assembly, typeof(JsonElement).Assembly, typeof(ImmutableArray<>).Assembly,
        typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly];

    /// <summary>根据当前入口的依赖清单创建解析器。</summary>
    public PluginLoadContext(string entryPath) : base(isCollectible: true) => _resolver = new AssemblyDependencyResolver(entryPath);

    /// <summary>共享跨边界类型，其余依赖保留在插件自己的上下文。</summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly? shared = Shared.FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name);
        if (shared is not null) return shared;
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>按 .deps.json 解析本机运行资产，不修改全局库搜索路径。</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
