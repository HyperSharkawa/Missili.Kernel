using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Tools.Shell;

/// <summary>解释器和环境由插件配置明确给出，不根据失败猜测另一解释器。</summary>
internal sealed record ShellSettings
{
    public required string Executable { get; init; }
    public required ImmutableArray<string> Arguments { get; init; }
    public int DefaultTimeoutSeconds { get; init; } = 180;
    public int MaximumTimeoutSeconds { get; init; } = 3600;
    public int MaximumOutputBytes { get; init; } = 1024 * 1024;
    public int MaximumPreviewBytes { get; init; } = 64 * 1024;
    public ImmutableArray<string> InheritedEnvironment { get; init; } = ["PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LANG", "LC_ALL"];
    public ImmutableDictionary<string, string> EnvironmentReferences { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>仅使用共享 JSON 解析私有配置，防止默认序列化缓存保活插件类型。</summary>
    public static ShellSettings Read(JsonElement configuration)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty field in configuration.EnumerateObject())
            if (!names.Add(field.Name) || field.Name is not ("executable" or "executableEnvironmentVariable" or "arguments" or "defaultTimeoutSeconds" or
                "maximumTimeoutSeconds" or "maximumOutputBytes" or "maximumPreviewBytes" or "inheritedEnvironment" or "environmentReferences"))
                throw new ArgumentException("Shell 配置字段重复或未声明：" + field.Name);
        bool direct = configuration.TryGetProperty("executable", out JsonElement executable);
        bool reference = configuration.TryGetProperty("executableEnvironmentVariable", out JsonElement variable);
        if (direct == reference) throw new ArgumentException("必须明确选择解释器路径或其环境变量引用。");
        string? path = direct ? executable.GetString() : Environment.GetEnvironmentVariable(variable.GetString() ?? throw new ArgumentException("解释器引用不能为空。"));
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException("解释器必须是存在的绝对文件路径。");
        ShellSettings defaults = new() { Executable = path, Arguments = ReadStrings(configuration.GetProperty("arguments")) };
        ShellSettings settings = defaults with
        {
            DefaultTimeoutSeconds = Number(configuration, "defaultTimeoutSeconds", defaults.DefaultTimeoutSeconds),
            MaximumTimeoutSeconds = Number(configuration, "maximumTimeoutSeconds", defaults.MaximumTimeoutSeconds),
            MaximumOutputBytes = Number(configuration, "maximumOutputBytes", defaults.MaximumOutputBytes),
            MaximumPreviewBytes = Number(configuration, "maximumPreviewBytes", defaults.MaximumPreviewBytes),
            InheritedEnvironment = configuration.TryGetProperty("inheritedEnvironment", out JsonElement inherited) ? ReadStrings(inherited) : defaults.InheritedEnvironment,
            EnvironmentReferences = configuration.TryGetProperty("environmentReferences", out JsonElement references)
                ? references.EnumerateObject().ToImmutableDictionary(item => item.Name, item => item.Value.GetString() ?? throw new ArgumentException("环境变量引用不能为空。"), StringComparer.Ordinal)
                : defaults.EnvironmentReferences
        };
        if (settings.DefaultTimeoutSeconds < 1 || settings.MaximumTimeoutSeconds < settings.DefaultTimeoutSeconds || settings.MaximumOutputBytes < 1 || settings.MaximumPreviewBytes < 512 ||
            settings.Arguments.Any(argument => argument.Contains('\0')) || settings.InheritedEnvironment.Any(InvalidEnvironmentName) ||
            settings.EnvironmentReferences.Any(item => InvalidEnvironmentName(item.Key) || InvalidEnvironmentName(item.Value)))
            throw new ArgumentException("Shell 期限、输出预算或环境变量配置无效。");
        return settings;
    }

    /// <summary>数值必须是真实整数，不把字符串或小数转换为配置值。</summary>
    private static int Number(JsonElement configuration, string name, int fallback) => configuration.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : fallback;

    /// <summary>拒绝操作系统无法表达的变量名，不把错误配置静默当作缺失值。</summary>
    private static bool InvalidEnvironmentName(string value) => string.IsNullOrWhiteSpace(value) || value.Contains('=') || value.Contains('\0');

    /// <summary>显式参数顺序原样传给解释器，数组成员不能为 null。</summary>
    private static ImmutableArray<string> ReadStrings(JsonElement value) => value.EnumerateArray().Select(item => item.GetString() ?? throw new ArgumentException("参数不能为 null。")).ToImmutableArray();
}
