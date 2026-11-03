namespace Fixture.PrivateDependency;

/// <summary>私有依赖夹具，两份同名程序集返回不同值以验证加载隔离。</summary>
public static class VersionInfo
{
    /// <summary>从实际私有程序集读取版本标记。</summary>
    public static string Read()
    {
#if FIXTURE_V2
        return "v2";
#else
        return "v1";
#endif
    }
}
