# OneBot v11 接入与协议联调

官方适配器是可热重载的编译插件 `missili.onebot11`。Host 发布目录包含 `plugins/onebot`，默认不启用。开发时可加载 `Missili.Adapters.OneBot/bin/Release/net10.0`。Host 仅将该项目作为构建依赖，不在默认程序集上下文加载适配器实现。

## 启用一个实例

在宿主 `Plugins` 数组中添加明确启用的包及配置文件：

```json
{
  "Plugins": [
    {
      "Enabled": true,
      "Directory": "plugins/onebot",
      "ConfigurationFile": "onebot.json"
    }
  ]
}
```

`onebot.json` 中可包含多个实例。每个实例的 `id` 必须稳定且唯一；更换它会改变历史、去重和媒体来源作用域。

```json
{
  "instances": [
    {
      "id": "qq-main",
      "botAccountId": "10001",
      "direction": "Forward",
      "layout": "Universal",
      "url": "ws://127.0.0.1:6700/",
      "authentication": "Token",
      "tokenEnvironmentVariable": "MISSILI_ONEBOT_TOKEN",
      "compatibilityProfile": "Standard"
    }
  ]
}
```

账号是配置中的字符串；协议消息身份按标准 JSON 整数读取，不经过浮点数转换。Token 默认启用，`token` 与 `tokenEnvironmentVariable` 必须且只能提供一个。Token 不写日志。合成无鉴权测试显式使用 `"authentication": "None"`，并删除两个 Token 来源字段。

`ws` 与 `wss` 均可使用，TLS 保持正常证书验证。正向连接建立后，适配器调用 `get_login_info` 核实账号；每条事件也检查 `self_id`。

## 四种连接组合

| 方向与布局 | 配置 | 连接角色 |
| --- | --- | --- |
| Forward / Universal | `url` 为完整地址 | 一条连接同时提供 API 和事件 |
| Forward / Split | `apiUrl`、`eventUrl` 为各自完整地址 | 两条独立连接 |
| Reverse / Universal | 不配置主动连接 URL | 对端连接 `/adapters/{id}/ws`，`X-Client-Role: Universal` |
| Reverse / Split | 不配置主动连接 URL | 对端向同一路径分别建立 `API`、`Event` 连接 |

正向分离实例示例：

```json
{
  "instances": [{
    "id": "qq-split", "botAccountId": "10001",
    "direction": "Forward", "layout": "Split",
    "apiUrl": "ws://127.0.0.1:6700/api",
    "eventUrl": "ws://127.0.0.1:6700/event",
    "authentication": "Token", "tokenEnvironmentVariable": "MISSILI_ONEBOT_TOKEN"
  }]
}
```

反向实例将 `direction` 改为 `Reverse` 并移除所有 URL 字段。在 OneBot 实现一侧配置 Missili 的地址，例如 `ws://127.0.0.1:8080/adapters/qq-split/ws`。`X-Self-ID`、`X-Client-Role` 和 `Authorization: Bearer ...` 由 OneBot 实现按标准提供。

未知或停用实例、错误账号、错误角色和鉴权失败均在升级前拒绝。同角色新连接验证通过后替换旧连接；旧连接的发送队列和回执不会转移到新连接。反向 HTTP 请求持续到 WebSocket 处理结束。宿主停止前置阶段先关闭插件连接，随后 Kestrel 等待请求退出。

正向断线采用 1 秒起、最大 30 秒的指数退避和 ±20% 抖动；账号验证成功后重置。反向重连由对端发起。OneBot 业务心跳可关闭，不用“没有收到业务心跳”推断离线。WebSocket 保活独立运行。

## 标准能力与兼容清单

协议依据是 [OneBot v11 固定提交 d4456ee](https://github.com/botuniverse/onebot-11/tree/d4456ee706f9ada9c2dfde56a2bcfc69752600e4)。支持状态区分接收保存、语义读取、后台获取和发送；与某个实现互通不等于该实现是标准。

| 标准能力 | 当前处理 |
| --- | --- |
| 群聊、私聊、数组、CQ 字符串 | 身份校验、规范化及持久化；数组值不再次 CQ 解码 |
| 文本、QQ 表情、提及、引用 | 保存、强类型访问及标准发送 |
| 图片、语音、视频 | 媒体来源持久化、后台归档、独立流读取、Base64 发送 |
| 分享、联系人、位置、戳一戳、猜拳、骰子 | 保留标准字段和额外载荷；发送只使用允许字段 |
| 抖动、匿名、音乐 | 标准发送方向；接收时保留原段并标记方向不符合标准 |
| JSON、XML 容器 | 完整保存容器内容，不推断内部 QQ 卡片格式 |
| 合并转发引用 | 保存后后台查询、节点归档、局部失败及限制记录 |
| 转发节点发送 | `SegmentValues.Forward` 构造标准 `node` 数组，不直接发送接收资源 ID |
| 全部标准通知、好友和入群请求 | 独立事件保存、查询和插件订阅；不自动批准 |
| 心跳、生命周期 | 更新运行状态，不逐条保存业务历史 |
| 未知消息段 | `onebot11.*` 类型及原载荷保留，未提供编码能力时拒绝发送 |
| 平台 API | 通过 `echo` 关联；查询、撤回、请求处理等经平台服务调用 |
| HTTP API、HTTP POST 上报 | 当前不提供 |

规范没有视频文件查询接口。视频没有直接 HTTP 来源或显式本地映射时，归档会说明不可取得原件。标准 `get_image`、`get_record` 返回对端路径，不代表 Missili 能直接读取该路径。

`compatibilityProfile: "SnowLuma"` 仅启用有依据的形态：

- `get_forward_msg` 的 `data.messages`，节点为 `sender`／`message`。标准形式仍是 `data.message` 与 `node` 段。
- 脱敏转发样本中只有 `url` 而缺少标准 `file` 的图片段。
- `get_record` 的 `data.base64` 返回；记录为转换后的指定格式。
- `message_sent` 自身发送观察事件，精确关联平台消息标识。

兼容行为依据已有适配器的脱敏转发样本和语音处理代码，标准形态与扩展形态分别测试。

已知的实现限制会影响以下操作：

- SnowLuma 2.4.0-node 的私聊撤回存在 `friend_recall.message_id` 与原消息标识不一致的情况。无法精确关联时，Missili 保存撤回事件并保留原消息状态，不按时间或正文猜测匹配。
- 在 SnowLuma 2.0.2-node 中，将普通文字、图片与内嵌 `node` 混在同一节点 `content` 中的发送形式存在 `retcode=1400` 拒绝情况。可使用标准引用节点 `{"type":"node","data":{"id":"已有消息 ID"}}` 构造嵌套转发；适配器不自动拆分消息。

样本来源为 `MaiBot-Snowluma-Adapter-For-0.12.2` 中的 `tests/test_forward_message.py`、`src/recv_handler/message_handler.py` 和 `src/utils.py`。本项目对应验收位于 `OneBotArchiveTests`；标准夹具独立位于协议和消息段测试中。

## 消息、媒体和转发

段类型使用稳定字符串与载荷版本。文字只在 `Text` 保存；文本的额外扩展字段可留在 JSON，但不重复正文。未知和无效段分别标记，其他正常段继续保存。关键词和命令仅匹配正常文本，不匹配诊断说明或卡片摘要。

撤回事件与历史可见性在同一事务中更新，撤回先到也会在原消息随后保存时应用。只在适配器、账号、平台和会话范围内关联。自身发送观察和回执形成一条规范历史，旧本地标识查询仍能定位该记录。

媒体先登记来源，后台下载。获取顺序为可用原件、直接 HTTP 来源、标准媒体查询、显式映射及已启用兼容扩展。映射默认空，示例结构如下：

```json
{
  "pathMappings": [
    { "remoteRoot": "/onebot/media", "localRoot": "/explicitly-mounted-media" }
  ],
  "recordFormat": "wav"
}
```

映射必须放在对应实例中。本地目录必须为绝对路径；路径及符号链接目标不得越过允许目录。优先保存取得的原始文件；仅能取得转换语音时，在 `Representation` 中保留 `format`、`original: false` 和转换来源。

转发归档以稳定节点路径保存，重复保存相同位置不重复创建媒体。每次远端获取后的结果与检查点先提交，再继续子引用。节点正文、媒体归档、失败、空内容和截断分别表达。已有失败不会因普通读取重试；宿主中断或适配器重载后的未完成任务可以恢复，但不会重新触发聊天回复。

## 发送结果与资源限制

统一发送先保存意图，再调用平台一次，最后记录结果。离线的新发送立即失败；排队取消属于发送前取消；网络传输开始后取消、超时或断线属于未知结果，不自动重发。`status: async` 表示受理，不能记成确认成功。成功响应还必须带有效 `message_id`。确认已取得而记录失败时，返回 `Recorded: false` 和准确说明。

迟到确认保留最多 5 分钟、每实例 256 项，可修正未知状态，不再次发送，也不重复首次确认通知。进程重启不补发消息或重放业务。

| 配置项 | 默认值 |
| --- | --- |
| `connectTimeout` / `identityTimeout` | 各 10 秒 |
| `requestTimeout` / `closeTimeout` | 30 秒 / 5 秒 |
| `maxPendingRequests` / `sendCapacity` / `eventCapacity` | 32 / 128 / 256 |
| `inboundByteBudget` / `maxMessageBytes` | 各 64 MiB |
| `memoryThreshold` | 1 MiB，超过后暂存到本任务文件 |
| `maxOutgoingMediaBytes` | 32 MiB，编码后还受 JSON 上限约束 |
| `keepAliveInterval` / `keepAliveTimeout` | 30 秒 / 15 秒 |
| `maxForwardDepth` / `maxForwardNodes` | 5 层，根为第 1 层 / 500 |
| `maxForwardSegments` / `maxForwardRequests` | 5,000 / 64 |
| `maxForwardBytes` / `jsonMaxDepth` | 16 MiB / 64 |

TimeSpan 配置使用 `"00:00:30"` 等格式，字节项使用整数。非法值和布局组合在初始化时拒绝。宿主 `Infrastructure.ContentConcurrency`、`ContentCapacity` 默认分别为 2、64；数据库和媒体下载额度沿用基础设施配置。

## 独立 Mock

```bash
dotnet run --project tools/Missili.OneBot.Mock -- tools/Missili.OneBot.Mock/scenarios/standard-reply.json
```

配合 `tests/container/onebot.json` 的合成无鉴权实例和关键词插件配置 `tests/container/keyword.json`，可完成 `ping → pong`。关键词插件仍需明确启用；默认宿主不会自行回复。

场景根字段为 `mode: server|client`、完整 `url`、`layout: Universal|Split`、`selfId`；可提供 `tokenEnvironmentVariable`。服务端额外提供 `/media` 合成文件。客户端模式主动向反向入口按布局建立一条或两条连接。

`steps` 支持：

- `event`：发送给定 `event` JSON，可设 `fragmentBytes`。
- `awaitAction`：等收到指定 `action`，用 `saveAs` 保存此次请求。
- `respond`：对保存的 `request` 返回指定 `status`、`retcode` 和 `data`。
- `abort`：断开指定 `role`。
- `assertCalls`：检查指定 `action` 已收到的 `count`。
- `signal`：等标准输入出现指定 `name`，便于人工或脚本控制延迟。

用 `manualActions` 指定不自动响应的操作，再组合 `awaitAction`／`respond` 可复现乱序、异步受理和回执丢失。`responses` 可按 `action` 和可选 `parameters` 精确匹配并返回固定结果，适用于合并转发分支失败、媒体查询等场景。场景顺序依赖实际连接和操作信号，不依赖固定睡眠。

测试 Mock 自行构造标准报文，不使用生产编码器生成预期结果。测试工程只通过构建产物加载 OneBot DLL，检查默认加载上下文及卸载弱引用。

## 数据库与契约升级

启动应用新的 EF migration，保留初始迁移。四种旧段转换为字符串，旧媒体关联迁到关系表，来源 URL 转为版本化描述；原消息标识、原件路径、转述和用户排序保留。禁止删库重建升级。

仅仍可完整表达为旧模型的数据允许降级；出现扩展段、平台事件、多媒体或别名等新语义时，降级在变更前拒绝，防止有损转换。当前插件契约为版本 3，旧插件需适配共享契约后重新编译，详见 [插件开发说明](plugin-development.md)。

技术参考：[WebSocket 标准](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/communication/ws.md)、[反向连接](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/communication/ws-reverse.md)、[消息段](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/message/segment.md)、[ASP.NET Core 请求生命周期](https://learn.microsoft.com/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)。
