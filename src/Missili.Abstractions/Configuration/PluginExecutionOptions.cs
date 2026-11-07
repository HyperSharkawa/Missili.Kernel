namespace Missili.Abstractions;

/// <summary>宿主插件执行额度；消息回调和工具／出站处理使用两个独立调度通道。</summary>
public sealed record PluginExecutionOptions
{
    /// <summary>清单并发声明的允许上限，清单省略时也采用此值；不表示跨通道的总并发。</summary>
    public int PluginConcurrencyLimit { get; init; } = 4;
    public PluginHandlerExecutionOptions Handlers { get; init; } = new();
    public PluginToolProcessorExecutionOptions ToolsAndProcessors { get; init; } = new();

    /// <summary>在创建队列或加载插件前校验两组额度，排队容量无需大于执行并发。</summary>
    public void Validate()
    {
        if (Handlers is null || ToolsAndProcessors is null || PluginConcurrencyLimit < 1)
            throw new ArgumentException("插件执行配置必须提供有效的通道和清单并发上限。");
        Handlers.Validate();
        ToolsAndProcessors.Validate();
        if (PluginConcurrencyLimit > Handlers.Concurrency)
            throw new ArgumentException("插件清单并发上限不能超过消息处理器全局并发。");
    }
}

/// <summary>消息拦截器、命令、消息监听器和平台事件回调的共享执行通道。</summary>
public sealed record PluginHandlerExecutionOptions
{
    public int Concurrency { get; init; } = 32;
    public int QueueCapacity { get; init; } = 512;

    /// <summary>校验实际调度器所需的正整数，不引入额外的容量比例约束。</summary>
    public void Validate()
    {
        if (Concurrency < 1 || QueueCapacity < 1) throw new ArgumentException("消息处理器并发和队列容量必须大于零。");
    }
}

/// <summary>普通 Agent 工具和所有出站处理器共用的执行通道，不依赖 Agent 启用状态。</summary>
public sealed record PluginToolProcessorExecutionOptions
{
    public int Concurrency { get; init; } = 8;
    public int PerPluginConcurrency { get; init; } = 2;
    public int QueueCapacity { get; init; } = 128;

    /// <summary>实际单插件额度再与插件声明取较小值，允许声明上限低于本通道额度。</summary>
    public void Validate()
    {
        if (Concurrency < 1 || PerPluginConcurrency < 1 || QueueCapacity < 1 || PerPluginConcurrency > Concurrency)
            throw new ArgumentException("工具和出站处理器的并发或队列容量无效。");
    }
}
