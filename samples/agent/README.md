# Agent 合成场景

`profile.json` 引用模型目录中的 `primary` 和 `fallback`，可配合 `samples/model-config/catalog.json` 使用。将配置档放入配置目录的 `profiles`，在 `routes.json` 显式选择 `agent`。宿主开启 Models 和 Agents，加载模型提供商、AgentChat、History、Reply、ReplyProcessing 及适配器。

示例采用 Direct，子代理不含发送工具，后处理规则为空。需要工具回复时将 `outputMode` 改为 `ToolsOnly`；需要自然分段时给 `missili.plugins.reply-processing.steps` 增加 `{"type":"split","maximumTextElements":800}`。shell 仅给出审批配置，仍需单独加载和开放 `shell.execute`。

省略 `agent.instructions` 使用内嵌米粒人设；显式字符串完整替换，空字符串关闭默认人格。回复与子代理模板提示词独立。默认 `/agent` 前缀或真实提及只唤醒主代理，自主决定回复、工作、拒绝或沉默。关键词默认空，概率默认关闭；管理命令与前缀无关，停止只允许当前 `administrators` 中的身份。

以下可复用测试场景使用正式入口、临时 SQLite、真实编译插件和本地协议对端，无需真实账号或模型。

| 场景 | `dotnet test --filter` 的 FullyQualifiedName 子串 | 控制方式 |
| --- | --- | --- |
| 基础任务与候选切换 | `AgentEntryTests.OneBotEntryRunsWholeAgentChain` | 顺序模型响应及平台回执 |
| 独立回复、正则分段、最终审批 | `AgentWorkflowTests.ToolsOnlyReplyProcessingApprovalAndReceipts` | 三个模型用途及逐条回执 |
| 子代理并行、通信、部分失败及 follow-up | `AgentWorkflowTests.ParallelChildrenCommunicateAndRecoverPartialFailure` | 两个请求同时进入信号屏障，消息消费后释放 |
| 运行中追加与停止 | `AgentWorkflowTests.OneBotStopKeepsPartialWorkAndOnlyOneSettlement` | 唤醒记录与管理员停止命令触发 |
| shell 拒绝、澄清与新调用获准 | `AgentWorkflowTests.DeniedShellClarifiesThenExecutesNewApprovedCallOnce` | 明确二元审批，实际临时标记证明只执行一次 |
| 未知发送、迟到回执与重启 | `AgentWorkflowTests.UnknownBatchLateReceiptAndRestartDoNotReplay` | 可控时钟及手动回执，新运行时读取旧数据库 |
| 四种唤醒与管理命令 | `AgentWakingTests` | 0%／100% 固定边界、规则短路、配置热更新及真实 DLL |
| 统一窗口与工具续接 | `AgentContextTests`、`AgentWakingTests.EvictedWakeNeverAppearsInFirstDecision` | 模型请求屏障和持久化淘汰边界，检查原始协议请求 |
| 三协议默认与覆盖提示词 | `AgentPromptTests` | 缺省、自定义、空字符串及群／私聊事实 |
| 既有运行数据升级 | `AgentStorageTests.UpgradeFromPhaseFour` | 临时旧库、迁移后关联与搜索结果 |

例如：

```sh
dotnet test tests/Missili.Tests/Missili.Tests.csproj -c Release --filter FullyQualifiedName~AgentWorkflowTests
```

模型协议 Mock 的 JSON/SSE 响应独立编写，不调用生产编码器生成预期。需要编写新的静态场景时，可设置 `ModelMockScenario.Steps`；每步支持 `model`、`path`、`status`、`response`、`events`、`waitForSignal`、`beforeEventSignals`、`fragmentBytes`。独立运行时将信号名输入模型 Mock 控制台即可推进。动态代理身份从真实工具结果读取，不能在场景中猜测宿主将生成的 Guid。

更多配置见 [Agent 使用说明](../../docs/agents.md)。
