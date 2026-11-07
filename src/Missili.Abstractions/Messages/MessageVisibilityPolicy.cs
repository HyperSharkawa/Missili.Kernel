using System.Collections.Immutable;
using System.Text.Json;

namespace Missili.Abstractions;

/// <summary>共享消息可见性策略，用于聊天投影、历史读取及动作证据；原始记录始终保留。</summary>
public enum MessageVisibilityPolicy { Placeholder = 0, MarkedOriginal = 1, Original = 2 }
