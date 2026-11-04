# 模型、会话上下文与媒体转述

在允许的会话中发送 `/model 问题`，测试插件会取得已入库的会话资料，按该会话配置选择模型，完整生成后通过统一发送服务回复一次。默认关闭模型模块和测试插件。普通消息只入库，不自动请求回复模型；启用后台媒体转述会按其配置请求识图或转写模型。

## 用两个本地 Mock 跑通完整链路

从仓库根目录构建宿主与显式测试插件：

```sh
dotnet publish Missili.Host -c Release -o artifacts/model-host
dotnet publish samples/Missili.Sample.ModelChat -c Release -o artifacts/model-host/plugins/model-chat
```

把 `samples/model-mock/host.json` 复制为 `artifacts/model-host/appsettings.json`。在宿主进程环境设置 `MISSILI_MODEL_MOCK_KEY=synthetic`；这是本地 Mock 使用的合成值。PowerShell 使用 `$env:MISSILI_MODEL_MOCK_KEY = 'synthetic'`，Bash 使用 `export MISSILI_MODEL_MOCK_KEY=synthetic`。

三个终端分别启动，工作目录均为仓库根目录：

```sh
dotnet run --project tools/Missili.Model.Mock -c Release -- --scenario samples/model-mock/fallback.json
dotnet run --project tools/Missili.OneBot.Mock -c Release -- samples/model-mock/onebot.json
dotnet artifacts/model-host/Missili.Host.dll --contentRoot "$PWD/artifacts/model-host" --urls http://127.0.0.1:24082
```

OneBot Mock 出现 `等待输入控制信号：go` 后，在其终端输入 `go`。场景发送背景资料和 `/model`，首候选返回 500，备用候选流式成功；OneBot Mock 检查只收到一次 `send_group_msg`。测试账号、群号均为合成身份。自动化等价场景位于 `ModelEndToEndTests`，还会检查真实 SQLite 中的消息、尝试、回执与上下文。

宿主发布包含 `plugins/onebot`、`plugins/openai`、`plugins/anthropic`，各有完整依赖；提供商实现与 SDK 不加载进宿主默认程序集上下文。测试回复插件单独发布并明确启用。

## 配置档决定完整行为

`Models.ConfigurationDirectory` 相对宿主内容根目录，目录内包含：

```text
catalog.json              提供商连接、模型 ID、能力与参数
routes.json               默认配置档和有序路由
profiles/default.json    一份独立会话配置档
profiles/private.json    另一份独立会话配置档
```

可运行示例在 `samples/model-config`。所有 JSON 使用 camelCase；拼错字段、无效引用、非法期限和容量都会拒绝发布。可选字段使用程序默认值，配置档之间没有隐式字段继承。

提供商保存 `id`、`protocol`、`baseUrl`、`apiKeyEnvironmentVariable`、`maxConcurrency`。只从明确命名的环境变量读凭据；目录、日志和排障包不保存凭据值。SDK 地址规则如下：

| 协议 | Base URL 示例 | 实际请求路径 |
| --- | --- | --- |
| `ChatCompletions` | `https://api.openai.com/v1` | `/v1/chat/completions` |
| `Responses` | `https://api.openai.com/v1` | `/v1/responses` |
| `AudioTranscription` | `https://api.openai.com/v1` | `/v1/audio/transcriptions` |
| `Anthropic` | `https://api.anthropic.com` | `/v1/messages` |

兼容服务按自身公开地址配置，不自动试探多个 URL。模型的 `id` 是框架引用，`name` 是实际传给服务的模型名称。`images`、`audio`、`tools`、`streaming` 由配置声明，不按模型名称猜测。音频对话编码目前由 Chat Completions 提供，文件为 WAV 或 MP3；STT 的 `audioFormats` 单独声明可接受的文件容器。

`parameters` 是协议扩展对象，可明确提供温度、推理参数或缓存选项。它不能覆盖模型、消息、工具、流式开关等框架控制字段。默认最大输出为 4096 tokens，温度与推理强度不主动设置。

配置档的 `replyModels`、`imageModels`、`transcriptionModels` 是有序模型 ID 列表。`streamReplies` 默认 true；语音转写使用对应模型的 `streaming` 值。不支持流式的服务需要明确设 false。

路由按完整的 `adapterId`、`platformId`、`botAccountId`、`conversationKind`、`conversationId` 匹配，各字段支持 `*` 和 `?`，区分大小写。首个匹配规则生效，无匹配时使用 `defaultProfileId`。共用配置档的会话仍隔离历史。

文件变化合并后重新校验，全部有效才发布新修订；无效修改保留上一有效配置并记录错误。也可调用 `IConversationProfileService.ReloadAsync`。在途调用固定原配置，新调用使用新修订；不重载全部插件或清空上下文。

## 上下文保留与生成结果

群内所有已接收成员消息及 Bot 已确认发送消息参与上下文。失败、待发送和未知发送不冒充成功。历史以带发言者、时间、消息身份和引用关系的会话资料表达，真实模型 assistant 轮次与群聊资料分开。

触发消息使用传播后的 `CurrentContent`，其他记录使用持久化快照。一次逻辑调用固定历史高水位，候选切换不会悄悄删掉历史；本地不计 token，不因服务报上下文超限而自动裁剪。

默认 `maximumMessages=100`、`discardMessages=20`：99 条继续使用，达到 100 条整批丢弃最早 20 条，保留 80 条。窗口边界落库，原历史不删除；重启或增大上限不会恢复已淘汰的消息。允许 `discardMessages=1`，校验要求 `1 ≤ y < x`、`x ≥ 2`。固定系统提示与展开节点不单独计数。实际提示缓存命中以服务返回的缓存用量为准。

`visibility` 的三种值是 `Placeholder`（只看撤回／删除占位）、`MarkedOriginal`（原文并标记，默认）、`Original`（原文无额外标记）。这只影响模型投影。视频始终使用实际标题和视频类型占位；未知消息段与转发局部失败保留可读说明。

`execution` 默认按 `A → B → 等待 2 秒 → A → B` 尝试，`rounds=2`、`roundDelay="00:00:02"`。SDK 内部重试关闭。HTTP、网络、协议和截断流失败可进入下一候选；主动取消、输入错误、增量消费者错误和本地记录失败会停止调用。

建连、响应头、连续读取空闲期限分别为 10、120、120 秒，没有整个生成过程的总时限。心跳字节能维持流活性，但不是文字。失败尝试的部分输出不拼到备用模型结果里。

检查 `ModelResult.State`、`Complete` 和内容块的 `Complete`。取消结果保留已经取得的内容；部分工具参数只可读，不能执行。`RecordingFailed` 可以携带已取得的完整结果，必须明确处理，不能重新生成来补偿存储故障。`/model` 在这种情况下只记录错误，不发送生成结果。

## 图片与语音转述

图片和语音分别配置 `images`、`audio`：

| 策略 | 行为 |
| --- | --- |
| `OriginalPreferred` | 候选支持时用原媒体，不额外强制转述；不支持时使用已有文字或明确占位 |
| `TextOnly` | 使用成功缓存；缺少时按转述开关等待生成，失败显示占位 |
| `TranscriptionPreferred` | 归档后后台转述；暂无缓存且候选支持时先用媒体，后续请求使用文字 |

转述还需启用 `imageTranscriptionEnabled`／`audioTranscriptionEnabled` 并配置对应候选。后台扫描只为混合模式安排转述，TextOnly 在输入需要时执行。普通状态查询不触发重试。

成功缓存键为原内容键和种类。同一内容的并发请求共享一次框架工作，一个等待者取消不影响其他等待者；业务插件重载只取消其等待。更换模型、提示词或预处理参数不自动失效。显式 `refresh: true` 成功后才替换旧结果，失败仍保留旧文字。提供商重载取消该代次的实际网络调用。

没有持久转述任务表或执行游标。进程中断后，下次启动按当前配置有限扫描没有成功结果的媒体，执行成功或失败即结束，不忙轮询。普通聊天插件不因此重放。

图片识别支持 JPEG、PNG、WebP、BMP，动画支持 GIF、APNG、动态 WebP。先应用方向、透明背景转白底，再压缩 JPEG。普通图等比缩小，长宽比达到 3 时沿长边切片并保留重叠与尾部；原件始终保留。

| `imagePreparation` 参数 | 默认值 |
| --- | --- |
| `quality` | 85 |
| `maxWidth`／`maxHeight` | 2048／2048 |
| `sliceRatio`／`overlap` | 3／64 像素 |
| `maxRequestBytes` | 8 MiB |
| `maxImagesPerRequest`／`maxSlices` | 20／128 |
| `framesPerSecond`／`maxSampleFrames` | 2／8 |
| `maxSourceBytes` | 128 MiB |
| `maxDecodedPixels`／`maxSourceFrames` | 64,000,000／512 |

动画按一个播放周期的合成画面与实际时长采样；到上限时均分整个周期，重复落在同一源帧只取一次，零时长按 10 毫秒处理。采样帧继续压缩和切片，8 帧不等于最多 8 张切片。

组批按 SDK 实际序列化的 UTF-8 JSON 体积，包含 Base64、提示词、位置说明和参数；同时满足模型与配置档的图片数及体积限制。单片超限明确失败，不无止境降质量或删除尾部。各批结果按原顺序合并，全部成功才写完整缓存；部分结果可读，不另加总结模型请求。

语音默认文件上限 `maxAudioBytes=25165824`（24 MiB）。原件格式可用则直接读取；需要转换时通过适配器能力请求 WAV，验证实际文件头并保存原内容键与派生媒体关系。OneBot 使用 `get_record(out_format=wav)`；只有对端路径且没有显式映射会明确失败。只剩 SILK 原件且原适配器不可访问时，报告格式准备失败。当前不提供基于 FFmpeg 或 SILK 解码器的本地转码。

## 插件调用与排障

插件从 `IPluginContext` 取得 `Models`、`ModelCatalog`、`Profiles`、`ConversationContext`、`MediaTranscription`、`MediaRepresentation`、`ModelCalls`，只依赖 Abstractions。完整消费者见 `samples/Missili.Sample.ModelChat`。

```csharp
ConversationContextSnapshot snapshot = await context.ConversationContext.CaptureAsync(message, token);
ModelResult result = await context.Models.GenerateAsync(new ModelRequest
{
    Purpose = "reply",
    Profile = snapshot.Profile,
    Context = snapshot,
    Candidates = snapshot.Profile.Profile.ReplyModels,
    Messages = [],
    Stream = snapshot.Profile.Profile.StreamReplies
}, cancellationToken: token);
```

需要增量时传入 `IModelUpdateSink`，回调异步消费并产生背压，没有无界增量队列。按 `CallId`、`AttemptId` 和 `ModelId` 区分输出归属。网络额度独立于消息处理器；准备图片、等待转述与轮间等待不占回复模型的网络槽。

模型结果中的 `Continuation` 保存原生 Items／内容块及来源。下一请求在准确位置添加 `new ModelMessage { Role = "assistant", Content = [], Continuation = result.Continuation }`，再添加一个或多个工具结果轮次。原生数据只允许所属协议、提供商和模型使用；不要与通用输出重复添加，也不要跨协议转换签名或推理项。模型服务提供协议往返，不自动执行工具。

`MediaTranscription.GetAsync` 读取状态；`EnsureAsync` 执行或刷新；失败时检查 `Error`、`PartialText`，旧缓存位于 `Cached`。`MediaRepresentation.PrepareAudioAsync` 返回可读取的媒体 ID 和实际 MIME。媒体流及调用详情流都由调用方用 `await using` 释放。

`ModelCalls.QueryCallsAsync` 支持会话、用途、状态、开始时间 `[StartedFrom, StartedUntil)` 和序号游标；用 `GetCallAsync` 读取尝试。每次调用和尝试保存元数据与可用用量，未知值为空，不据此声称精确账单。

失败尝试默认保存详情 ZIP；`execution.captureSuccessfulDetails=true` 也保留成功详情。通过 `OpenDetailAsync(attemptId)` 读取：

- `input.json`：本次绑定的参数与共享输入。
- `request.json`：实际协议参数与文本，媒体 Base64 被原件引用替代。
- `response.bin`：实际取得的原始响应或 SSE 字节，包括失败片段。
- `outcome.json`：完整性、结果、HTTP 状态、请求 ID 和收到的字节数。

详情位于数据目录的 `model-calls`，只用于排障，不参与任务恢复。成功默认删除临时详情；先前失败候选的详情保留。调用前无法保存必要记录时不发请求。

## 编写模型 Mock 场景

`steps` 按实际请求次数选择，耗尽后使用 `default`；`strictSteps=true` 时多余请求返回 409。每一步可以校验 `model`／`path`，设置 `status`、`text`、`textParts`、`fragmentBytes`、`truncated`，或直接给 `response`／`events` 指定原生 JSON／SSE。

`waitForSignal` 暂停响应头；`beforeEventSignals` 将从 0 起的事件位置映射到信号名称，用于暂停流。控制台输入名称后继续；集成测试调用 `Release`。`NextRequestAsync` 等待真实请求，避免用固定睡眠猜时序。`delayMilliseconds` 可演示慢流，自动测试优先使用信号与受控时钟。请求历史最多保留 256 项；独立长期场景需要持续消费请求通知，控制台入口会自动消费。

标准夹具独立于生产编码器。兼容服务的扩展或差异使用单独场景记录，便于分别验证标准协议与实现行为。

## 数据升级与依赖

迁移 `20261104002700_ModelContextAndCalls` 新增窗口、调用、尝试和媒体派生关联；旧转述单模型来源转为一个 `Sources` 条目，聊天、媒体、转述文本和编排保留。插件契约升级为 3，清单仍为 1，版本 1／2 插件需重新编译。

提供商使用 OpenAI 2.14.0、Anthropic 13.4.0，图片使用仓库本地包源中的 ImageSharp 源码构建包。ImageSharp 许可与第三方声明保存在 `.nupkg` 包内，补丁和修改说明位于 `third-party/imagesharp`；文件位置、设计参考与源码来源见 [第三方来源](third-party-sources.md)。
