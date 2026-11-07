using System.Text.Json;
using Fixture.PrivateDependency;
using Missili.Abstractions;

namespace Fixture.Plugin;

/// <summary>实际编译的工具夹具，通过私有依赖版本证明加载代次切换。</summary>
internal sealed class FixtureTool : IAgentToolHandler
{
    private readonly string _mode;

    /// <summary>模式只控制合成验收，正常工具不使用文件或外部账号。</summary>
    private FixtureTool(string mode) => _mode = mode;

    /// <summary>直接依据参数契约定义模式，不从生产校验器反向生成测试预期。</summary>
    internal static void Register(IPluginContext context, string mode)
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"],"additionalProperties":false}""");
        context.Tools.Register(new AgentToolDefinition
        {
            Id = "fixture.tool",
            Name = "fixture_tool",
            Description = "合成整数处理",
            OutputDescription = "返回整数和实际插件依赖版本",
            Parameters = schema.RootElement,
            ExecutionTimeout = null,
            PermissionCategory = "test.read"
        }, new FixtureTool(mode));
    }

    /// <summary>严格检查真实字段，不把数字字符串当作整数，也不允许隐藏未知参数。</summary>
    public Task<PreparedToolCall> PrepareAsync(AgentToolContext context, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1 ||
            !arguments.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            return Task.FromResult(new PreparedToolCall { Error = "value 必须是唯一的整数字段。", ErrorPath = "$.value" });
        return Task.FromResult(new PreparedToolCall { Arguments = arguments.Clone(), ExecutionDescription = "读取合成整数并返回当前插件依赖版本。" });
    }

    /// <summary>阻塞模式等待真实模型 Mock 的控制信号，用于验证取消与实际退出。</summary>
    public async Task<ToolExecutionResult> ExecuteAsync(AgentToolContext context, PreparedToolCall prepared, CancellationToken cancellationToken)
    {
        if (_mode is "tool-wait" or "tool-ignore-cancellation")
        {
            ModelResult model = await context.Models.GenerateAsync(new ModelRequest
            {
                Purpose = "fixture.tool",
                Profile = context.Profile,
                Candidates = ["A"],
                Origin = context.Origin,
                Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock("等待合成控制信号")] }])
            }, cancellationToken: _mode == "tool-ignore-cancellation" ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            if (model.State == ModelCallState.Cancelled) return new ToolExecutionResult { State = ToolCallState.CancelledDuringExecution, Complete = false, Text = model.Text };
        }
        if (_mode == "tool-throw") throw new InvalidOperationException("合成工具执行失败。");
        int value = prepared.Arguments!.Value.GetProperty("value").GetInt32();
        return new ToolExecutionResult
        {
            State = ToolCallState.Succeeded,
            Data = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["value"] = value, ["version"] = VersionInfo.Read() })
        };
    }
}
