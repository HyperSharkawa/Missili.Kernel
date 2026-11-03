# 编写 Missili 编译插件

插件引用 `Missili.Abstractions`，目标框架为 `net10.0`，实现具有公开无参构造函数的 `IPlugin`。可直接从 `samples` 的两个工程开始。

## 包与初始化

清单字段为 `schemaVersion`、`id`、`name`、`version`、`entryAssembly`、`entryType`、`contractVersion`，以及可选的 `maxConcurrency`。清单格式 `schemaVersion` 为 1，插件契约 `contractVersion` 为 **2**。旧插件需要针对新版共享契约重新编译；宿主不提供版本 1 包装层。

- `id` 是稳定身份，不能随展示名称或版本变化。
- `entryAssembly` 是包内相对 DLL 路径，`entryType` 是完整 CLR 类型名。
- `version` 用于展示，不能代替契约版本检查。
- `maxConcurrency` 可将当前插件的调用上限降低到 1，不能超过宿主的单插件上限。
- 示例开启 `EnableDynamicLoading`，构建输出包含依赖清单和运行依赖。
- 共享契约及跨边界的框架抽象使用宿主版本，不将另一份契约加载到插件私有上下文。

`InitializeAsync` 通过 `IPluginContext` 获得历史、媒体、发送、日志、配置、注册和任务服务。处理器只能在初始化时注册，初始化全部成功后才一次性对外发布。

配置是独立的 `JsonElement` 对象。插件自行检查必填字段，不把无效输入静默转换为默认值。不要将插件类型注册进宿主的永久 DI、EF 模型或序列化缓存。

插件私有 DTO 使用插件自己的源生成 `JsonSerializerContext`，显式传递对应的 `JsonTypeInfo`。不要把私有匿名类型交给 `JsonSerializer` 默认反射缓存；元数据可能阻止程序集回收。共享字典、JSON 值和 Abstractions DTO 可以跨边界传递。

## 消息与处理器

每次调用取得 `MessageContext`：

- `OriginalMessage` 是已入库的不可变原始快照。
- `CurrentContent` 是当前传播链的有序内容。
- `Kind` 是稳定字符串，`Version` 表示载荷版本；真实文字在 `Text`，非文本或扩展字段在 `Payload`。
- `MediaIds` 是零个或多个原件引用，媒体种类独立于段类型；`ContentId` 指向后台归档的结构化内容。
- `ParseState` 区分正常、未知和解析失败。关键词和命令仅匹配 `Kind == SegmentTypes.Text && ParseState == SegmentParseState.Parsed` 的正文。
- 常用内容通过 `SegmentValues.Text`、`Mention`、`Reply`、`Face`、`Share`、`Media` 和 `Forward` 构造。提及和引用可用 `MentionTarget()`、`ReplyTarget()` 读取。

处理器 ID 在插件内必须唯一且稳定。同一处理器改名时保留其 ID，才能继续使用用户保存的顺序。

| 注册方式 | 执行语义 |
| --- | --- |
| `RegisterInterceptor` | 当前消息内顺序执行，可返回继续、替换内容或显式接管 |
| `RegisterListener` | 放行后独立执行，不拥有拦截其他处理器的能力 |
| `RegisterCommand` | 声明明确的文本命令名，可附加匹配谓词；匹配成功后先接管，再执行 |
| `RegisterEventListener` | 独立监听已经持久化的平台通知和请求，异常仅影响自身 |

当前实际钩子是 `message.received`、`message.sent` 和 `platform.event`。平台事件只能登记独立监听，不能冒充聊天消息。通过 `context.Events` 分页查询或读取指定事件。命令名匹配首个真实文本词，使用区分大小写的精确比较。

默认优先级数值越大越先。用户编排覆盖默认顺序；并行监听的优先级不保证开始或完成顺序。命令与拦截器属于各自阶段，不能靠优先级越过内核存储步骤。

拦截器正常返回之后才应用替换内容，不能修改原始来源字段。抛出异常时不提交部分修改，框架记录错误并继续后续传播。普通监听失败只结束自身调用。

附加命令匹配谓词失败时继续传播；命令已经接管之后执行失败，不再自动交给另一条普通回复链。

## 并发与取消

默认同一插件实例可能同时处理最多 4 个调用。共享可变状态需要由插件同步，不能共享并发使用的 `DbContext`。需要串行运行的插件可以在清单中声明 `maxConcurrency: 1`。

所有异步调用传递收到的 `CancellationToken`。收到取消后退出当前工作，并释放当前调用持有的流或其他资源。令牌仅表示取消请求，不代表代码已经退出。

后台任务通过 `context.Tasks.Run` 登记，需释放的长期资源通过 `context.Tasks.Track` 登记。返回的资源句柄支持提前异步释放，释放成功后会注销登记。不要使用未登记的常驻任务、定时器或线程来承载插件生命周期。

网络连接等需要初始化完成后运行的任务使用 `context.Tasks.RunAfterActivation`。适配器在初始化中通过 `context.Adapters.Register` 暂存，全部成功后一起发布；初始化阶段不等待远端上线。状态变化调用 `NotifyChanged()`，用于恢复等待适配器的归档工作。

`StopAsync` 用于通知插件自有工作停止，可能与正在取消的调用并行。不要在仍有调用使用资源时自行破坏共享状态；登记资源会在在途工作结束后由宿主回收。

重载先停止旧代次接单，再取消并等待现有工作。默认 30 秒后仍未退出则重载失败，旧插件保持不可接单，其他插件继续运行。不会强制中止线程、并行启动相同插件新版本或自动回滚旧版本。

程序集卸载是协作式的。宿主清理注册、实例、委托和作用域后发起 `Unload()`；回收诊断通过弱引用观察，不在普通消息路径强制 GC。

## 原件与转述

取得媒体引用不需要等待下载。需要原件时调用 `IMediaStore.OpenReadAsync`：

- 未完成时等待现有归档。
- 成功时返回当前调用独占的只读流。
- 获取失败、已清理或文件不可读时返回明确错误。

使用 `await using` 释放 `MediaReadResult`，继续读取流时也传入调用令牌。取消当前等待不会取消其他调用者共享的归档任务。

插件生成媒体后使用 `context.MediaImporter.ImportAsync` 导入流，取得 `MediaStatus.Id` 再构造发送段。导入与下载共用内容去重及文件提交；导入不创建虚假的入站消息。

转发段的 `ContentId` 可交给 `context.Contents.GetContentAsync` 查询部分进度，或 `WaitContentAsync` 等待完整性状态。`Complete`、`Partial`、`Failed`、`Limited` 含义不同，`Issues` 带有具体节点位置和可读说明。`Nodes` 的路径保留嵌套位置；节点正文完成并不表示其中媒体已经归档。

引用回复使用 `RequestContentAsync(new ContentSource { Kind = "reply", Address = address, PlatformId = messageId })` 显式补全：先读本地，缺失才查询平台。补取内容不会进入消息监听器。普通读取不会自动重试已明确失败的归档。

`IMediaTranscriptionStore` 只存储当前成功转述与最近失败信息。缓存键由内容键和转述类型组成，更换模型或提示词不会自动失效。更新必须携带读取到的修订号；冲突后重新读取，不直接覆盖。

当前阶段没有实际识图、STT 或视频转述提供商，不要把“没有转述记录”当作错误。

## 历史与发送

历史查询显式选择接收时间或平台发生时间，以及是否包含撤回、删除和未确认发送记录。页大小默认 100、最大 500；邻近查询前后各不超过 50。返回值是普通快照，不是 EF 实体或 `IQueryable`。

发送统一使用 `IMessageSender`。只有发送意图保存成功后才调用传输，且只调用一次。检查 `SendResult.State` 与 `Recorded` 两项：平台已经确认但记录失败时，不能再次发送来补偿。

结果未知不自动重试。发送前取消与发送开始后的取消含义不同，后者不一定能证明平台未执行。

发送调用不会同步等待 `message.sent` 监听器，避免单并发插件等待自身造成死锁。发送后监听器如果继续发送，应明确避免递归触发无限回复。

宿主直接发起的发送也受关闭生命周期管理：停止接收新发送、请求取消，并等待实际传输与结果记录退出后才关闭存储。

适配器提交的通用消息显式保留方向。观察到的已发生发送事件按 `Outgoing` 保存并进入发送后钩子，不作为新的用户输入。自身上报与发送回执只按完整作用域中的平台消息标识合并；旧本地标识继续指向规范记录。

查询、撤回和请求处理通过 `context.Platform` 或 `OneBotOperations` 常用辅助方法调用。普通消息始终走 `context.Sender`，平台操作入口拒绝标准消息发送别名。平台操作不是 Agent 工具注册，不自动开放给模型。

## 验证插件

测试使用实际编译包，通过 `IPluginManager` 加载，不将待卸载 DLL 静态引用进测试程序集。核心单元场景可用 `IMessageTransport` 替代外部平台；协议联调使用 [独立 OneBot Mock](onebot.md)，以便覆盖完整网络边界。

至少验证普通运行、取消、异常、初始化失败、停止、同版本重载、更新版本和原件等待。对于热重载，必须证明旧程序集能够实际回收，不能只断言调用过 `Unload()`。
