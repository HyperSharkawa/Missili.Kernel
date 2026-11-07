namespace Missili.Tests;

/// <summary>测试与宿主使用相同的发布包目录，调用方只指定清单中的包名。</summary>
internal static class OfficialPluginPackages
{
    /// <summary>直接定位当前构建发布的包，不搜索旧分类目录或其他构建配置。</summary>
    public static string GetPath(string package) => Path.Combine(AppContext.BaseDirectory, "plugins", package);
}
