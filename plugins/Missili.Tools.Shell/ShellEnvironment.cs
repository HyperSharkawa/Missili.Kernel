using System.Security.Cryptography;
using System.Text.Json;

namespace Missili.Tools.Shell;

/// <summary>用代次私有密钥绑定实际环境，审批记录只保存校验标签，不保存环境变量值。</summary>
internal sealed class ShellEnvironment : IAsyncDisposable
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <summary>采集显式允许的环境，并复用目标平台对变量名的大小写规则。</summary>
    public static Dictionary<string, string> Capture(ShellSettings settings)
    {
        Dictionary<string, string> values = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string name in settings.InheritedEnvironment)
            if (Environment.GetEnvironmentVariable(name) is string value) values[name] = value;
        foreach (KeyValuePair<string, string> item in settings.EnvironmentReferences)
            values[item.Key] = Environment.GetEnvironmentVariable(item.Value) ?? throw new InvalidOperationException("明确配置的环境变量引用未设置：" + item.Value);
        return values;
    }

    /// <summary>校验标签用于批准与执行的完整性绑定，未知密钥防止从低熵变量值反推环境内容。</summary>
    public string Seal(Dictionary<string, string> values)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
        try { return Convert.ToHexString(HMACSHA256.HashData(_key, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>实际调用退出后由 Nexus 释放密钥，停止回调不能提前破坏在途校验。</summary>
    public ValueTask DisposeAsync() { CryptographicOperations.ZeroMemory(_key); return ValueTask.CompletedTask; }
}
