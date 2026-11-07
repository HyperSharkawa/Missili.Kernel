using System.Collections.Immutable;

namespace Missili.Abstractions;

/// <summary>主代理完整终答的用途由会话显式决定，不根据插件可用性自动切换。</summary>
public enum AgentOutputMode { Direct, ToolsOnly }
