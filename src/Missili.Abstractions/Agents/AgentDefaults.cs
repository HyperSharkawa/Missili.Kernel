using System.Text;

namespace Missili.Abstractions;

/// <summary>主代理缺省提示词的唯一程序集资源；显式配置（包括空字符串）不会追加此文本。</summary>
public static class AgentDefaults
{
    public static string Instructions { get; } = ReadInstructions();

    /// <summary>一次读取随程序集发布的 UTF-8 资源，缺失资源属于构建错误而不是配置回退。</summary>
    private static string ReadInstructions()
    {
        using Stream stream = typeof(AgentDefaults).Assembly.GetManifestResourceStream("Missili.Agent.DefaultInstructions")
            ?? throw new InvalidOperationException("主代理默认提示词资源缺失。");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
