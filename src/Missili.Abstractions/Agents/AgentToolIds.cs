using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>框架工具使用稳定身份，协议展示名称和插件加载代次不参与权限配置。</summary>
public static class AgentToolIds
{
    public const string Spawn = "agent.spawn";
    public const string List = "agent.list";
    public const string Message = "agent.message";
    public const string Followup = "agent.followup";
    public const string Wait = "agent.wait";
    public const string Stop = "agent.stop";
    public const string FinishTurn = "agent.finish_turn";
    public const string ReadContent = "content.read";
    public const string ReadTranscription = "transcription.read";
}
