using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missili.Abstractions;

namespace Missili.Kernel;

/// <summary>统一审核冻结动作；审批模型没有工具，业务执行由调用方在保存结论后完成。</summary>
public sealed class ToolApprovalService : IToolApprovalService
{
    private readonly IModelService _models;
    private readonly IAgentStore _agents;

    /// <summary>复用真实模型服务与根任务预算，不创建审批任务恢复系统。</summary>
    public ToolApprovalService(IModelService models, IAgentStore agents) { _models = models; _agents = agents; }

    /// <summary>只接受完整二元结果，合法拒绝不重新请求模型，所有故障均保持未放行。</summary>
    public async Task<ToolApprovalResult> ReviewAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return new ToolApprovalResult { State = ToolApprovalState.Cancelled, ErrorKind = "Cancelled" };
        if (!request.Prepared.Valid) return Failure("Input", "实际参数尚未通过准备阶段。");
        request = request with { Prepared = request.Prepared.Freeze(), Evidence = request.Evidence.Select(item => item with { Content = item.Content.Clone() }).ToImmutableArray() };
        EffectiveToolApproval policy = request.Profile.Profile.Approvals.Resolve(request.ToolId, request.Profile.Profile.Execution);
        if (!policy.Enabled) return new ToolApprovalResult { State = ToolApprovalState.NotRequired };
        Guid callId = Guid.NewGuid();
        try
        {
            if (request.Origin.AgentId is Guid agent && !await _agents.ReserveCallsAsync(agent, 1, 0, false, false, cancellationToken).ConfigureAwait(false))
                return Failure("Budget", "当前任务没有可用于审批的模型调用额度，或已经停止。");
            string selectedPolicy = policy.Policy.CustomText ?? ApprovalPrompts.DefaultPolicy +
                (policy.Policy.AdditionalText is null ? "" : "\n\n针对当前工具的补充政策：\n" + policy.Policy.AdditionalText);
            string evidence = JsonSerializer.Serialize(new ApprovalInput
            {
                ToolId = request.ToolId,
                Description = request.Description,
                Generation = request.Generation,
                PermissionCategory = request.PermissionCategory,
                HasSideEffects = request.HasSideEffects,
                GrantedAdditionalPermissions = request.Prepared.RequiredPermissions,
                Arguments = request.Prepared.Arguments!.Value,
                ExecutionDescription = request.Prepared.ExecutionDescription,
                Conversation = request.Conversation,
                Origin = request.Origin,
                Evidence = request.Evidence
            });
            ModelResult result = await _models.GenerateAsync(new ModelRequest
            {
                Id = callId,
                Purpose = "tool_approval",
                Origin = request.Origin,
                Profile = request.Profile with { Profile = request.Profile.Profile with { Execution = policy.Execution } },
                Candidates = policy.Models,
                Stream = policy.Stream,
                Conversation = request.Conversation,
                Input = ModelInput.FromTurns([new ModelMessage { Role = "user", Content = [ModelBlock.TextBlock(evidence)] }],
                    ApprovalPrompts.Framework + "\n\n当前有效审批政策：\n" + selectedPolicy)
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.State == ModelCallState.Cancelled)
                return new ToolApprovalResult { State = ToolApprovalState.Cancelled, ModelCallId = callId, ErrorKind = "Cancelled", Error = result.Error };
            if (result.State != ModelCallState.Succeeded || !result.Complete)
                return Failure(result.State == ModelCallState.RecordingFailed ? "Storage" : "Model", result.Error ?? "审批模型没有完整结束。", callId);
            if (result.Content.Any(block => block.Kind == ModelBlockKind.ToolCall)) return Failure("Format", "审批者返回了不允许执行的工具调用。", callId);
            using JsonDocument document = JsonDocument.Parse(result.Text, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("decision", out JsonElement decision) || decision.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("reason", out JsonElement reason) || reason.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(reason.GetString()))
                return Failure("Format", "审批结果必须恰好包含 decision 和非空 reason。", callId);
            ToolApprovalState state = decision.GetString() switch
            {
                "allow" => ToolApprovalState.Allowed,
                "deny" => ToolApprovalState.Denied,
                _ => ToolApprovalState.Failed
            };
            return state == ToolApprovalState.Failed ? Failure("Format", "审批决定必须是 allow 或 deny。", callId)
                : new ToolApprovalResult { State = state, Reason = reason.GetString(), ModelCallId = callId };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new ToolApprovalResult { State = ToolApprovalState.Cancelled, ModelCallId = callId, ErrorKind = "Cancelled" }; }
        catch (JsonException) { return Failure("Format", "审批模型返回了无效 JSON。", callId); }
        catch (ModelInputException exception) { return Failure("Input", exception.Message, callId); }
        catch (InvalidOperationException exception) { return Failure("Unavailable", "审批服务当前不可用：" + exception.GetType().Name, callId); }
        catch (Exception exception) { return Failure("Infrastructure", "审批基础设施失败：" + exception.GetType().Name, callId); }
    }

    /// <summary>只比较本动作会用到的配置，不比较无关的全局修订号。</summary>
    public bool IsCurrent(ToolApprovalRequest request, ConversationProfileSnapshot current)
    {
        EffectiveToolApproval previous = request.Profile.Profile.Approvals.Resolve(request.ToolId, request.Profile.Profile.Execution);
        EffectiveToolApproval next = current.Profile.Approvals.Resolve(request.ToolId, current.Profile.Execution);
        if (!previous.Enabled && !next.Enabled) return true;
        foreach (ToolApprovalEvidence evidence in request.Evidence)
        {
            if (evidence.Kind != "user_message" || evidence.SenderId is not string sender || evidence.Content.ValueKind != JsonValueKind.Object ||
                !evidence.Content.TryGetProperty("configured_administrator", out JsonElement administrator)) continue;
            if (administrator.GetBoolean() != current.Profile.Agent.Administrators.Contains(sender, StringComparer.Ordinal)) return false;
        }
        EffectiveToolApproval previousDecision = previous with { Execution = previous.Execution with { CaptureSuccessfulDetails = false } };
        EffectiveToolApproval nextDecision = next with { Execution = next.Execution with { CaptureSuccessfulDetails = false } };
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(previousDecision), JsonSerializer.SerializeToNode(nextDecision))) return false;
        foreach (string id in previous.Models)
        {
            if (!request.Profile.Models.TryGetValue(id, out ModelDefinition? oldModel) || !current.Models.TryGetValue(id, out ModelDefinition? newModel)) return false;
            if (oldModel.Name != newModel.Name || oldModel.ProviderId != newModel.ProviderId || oldModel.Streaming != newModel.Streaming ||
                !JsonNode.DeepEquals(oldModel.Parameters.HasValue ? JsonNode.Parse(oldModel.Parameters.Value.GetRawText()) : null,
                    newModel.Parameters.HasValue ? JsonNode.Parse(newModel.Parameters.Value.GetRawText()) : null)) return false;
            ModelProviderDefinition oldProvider = request.Profile.Providers[oldModel.ProviderId];
            ModelProviderDefinition newProvider = current.Providers[newModel.ProviderId];
            if (oldProvider.Protocol != newProvider.Protocol || oldProvider.BaseUrl != newProvider.BaseUrl ||
                oldProvider.ApiKeyEnvironmentVariable != newProvider.ApiKeyEnvironmentVariable) return false;
        }
        return true;
    }

    /// <summary>运行时失败不能伪装为审批模型作出的政策拒绝。</summary>
    private static ToolApprovalResult Failure(string kind, string message, Guid? callId = null) => new()
    { State = ToolApprovalState.Failed, ErrorKind = kind, Error = message, ModelCallId = callId };

    /// <summary>固定输入字段把政策之外的实际参数、用户材料和宿主身份作为数据组织。</summary>
    private sealed record ApprovalInput
    {
        public required string ToolId { get; init; }
        public required string Description { get; init; }
        public required string PermissionCategory { get; init; }
        public required bool HasSideEffects { get; init; }
        public required ImmutableArray<string> GrantedAdditionalPermissions { get; init; }
        public Guid? Generation { get; init; }
        public required JsonElement Arguments { get; init; }
        public string? ExecutionDescription { get; init; }
        public required MessageAddress Conversation { get; init; }
        public required OperationOrigin Origin { get; init; }
        public required ImmutableArray<ToolApprovalEvidence> Evidence { get; init; }
    }
}
