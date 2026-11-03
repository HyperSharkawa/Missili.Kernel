# Missili.Kernel

基于 .NET 10 的插件化 Bot 基础设施，采用 AGPL-3.0 许可证。

当前提供可扩展消息与平台事件持久化、媒体及合并转发归档、用户编排、编译插件热重载及 OneBot v11 适配器。正向、反向 WebSocket 均支持 Universal 和 API／Event 分离布局。协议联调使用独立 Mock；真实 QQ 账号兼容验收单独进行。模型、Agent、MCP 和 WebUI 留待后续阶段。

## 构建与运行

开发需要 .NET 10 SDK：

```bash
dotnet tool restore
dotnet restore Missili.Kernel.slnx
dotnet build Missili.Kernel.slnx -c Release --no-restore
dotnet test tests/Missili.Tests/Missili.Tests.csproj -c Release --no-build --no-restore
dotnet run --project Missili.Host
```

完整测试包含一次持续超过一分钟的本地媒体传输。测试使用临时数据库、合成数据及回环 HTTP／WebSocket 服务，不需要真实账号或模型。

宿主提供：

- `GET /health/live`：进程存活。
- `GET /health/ready`：基础设施初始化完成且存储可用。

默认不加载适配器或示例回复插件。发布目录包含 `plugins/onebot` 官方编译包，需明确配置启用。健康响应不包含聊天、配置或本地路径；适配器离线不使存储就绪检查失败。

OneBot 配置、能力清单与协议 Mock 见 [OneBot 接入说明](docs/onebot.md)。

## 数据与配置

配置位于 `Missili.Host/appsettings.json`。`Infrastructure.DataDirectory` 默认是内容根目录下的 `data`；相对路径以 ASP.NET Core 内容根目录解析。

- `history.db` 保存聊天历史、平台事件、结构化内容、媒体引用、转述结果与用户编排。
- `media` 保存原件和可恢复的暂存文件。
- `plugins` 保存安装包和独立运行代次。

历史与媒体默认不自动删除。撤回、删除只标记状态并保留原文。本阶段没有管理员清理入口。

主要初值：消息流程和插件调用全局各最多 32 个；单插件默认最多 4 个调用；媒体下载最多 4 个；SQLite 使用单写工作与最多 4 个并发读取。队列满时等待容量，不通过丢弃消息处理拥塞。

媒体连接、响应头、连续无数据期限分别为 10、30、60 秒。持续传输没有整包总时限。插件重载停止等待默认为 30 秒。

## 插件开发

示例工程：

- [关键词回复](samples/Missili.Sample.Keyword)：只读取文字，不等待媒体原件。
- [历史查询](samples/Missili.Sample.History)：通过历史服务查询有限记录。

关键词示例配置需要 `keyword` 和 `reply` 两个非空字符串；历史查询示例使用空配置对象，并注册 `/history` 命令。

```bash
dotnet build samples/Missili.Sample.Keyword -c Release
dotnet build samples/Missili.Sample.History -c Release
```

构建产物包含 `plugin.json`、入口 DLL、`.deps.json` 及依赖。分发 ZIP 时，`plugin.json` 必须直接位于 ZIP 根目录，保留依赖的相对布局。

宿主也可加载解包后的目录。通过 `Plugins` 配置数组明确启用：

```json
{
  "Plugins": [
    {
      "Enabled": true,
      "Directory": "path/to/compiled-plugin",
      "ConfigurationFile": "path/to/plugin-config.json"
    }
  ]
}
```

这两个路径同样相对于内容根目录。省略 `ConfigurationFile` 表示使用空 JSON 对象；提供文件时必须是合法 JSON 对象，不会忽略错误或自动改写。

编译插件的使用端不需要 SDK。本阶段可通过 `IPluginManager` 管理服务安装、启停和重载；没有开放管理 HTTP API。示例的发送通过宿主服务执行，没有平台传输时明确返回不可用。

完整契约见 [插件开发说明](docs/plugin-development.md)。

## 项目职责

| 项目 | 职责 |
| --- | --- |
| Missili.Abstractions | 平台无关消息及插件服务契约 |
| Missili.Persistence | EF Core、SQLite、迁移和索引查询 |
| Missili.Matrix | 有界公平调度 |
| Missili.Nexus | 编译包、加载代次和实际程序集卸载 |
| Missili.Kernel | 接收、媒体、消息分发和发送状态 |
| Missili.Host | 配置、组装、生命周期及健康接口 |
| Missili.Adapters.OneBot | 可动态加载的 OneBot v11 官方适配器 |
| Missili.OneBot.Mock | 独立协议对端及合成场景 |

初始数据库通过 EF Core migrations 创建，不使用 `EnsureCreated`。检查模型与迁移是否一致：

```bash
dotnet ef migrations has-pending-model-changes --project Missili.Persistence --startup-project Missili.Persistence
```

## 行为边界

- 消息写库失败，不进入插件处理；媒体下载失败保留消息并标记失败。
- 拦截器异常记录错误并继续传播；正常返回的显式拦截仍生效。
- 普通监听器异常只影响自身调用。
- 消息原文与后续处理内容分离，插件修改不会覆盖历史。
- 发送结果未知不自动重发，重启不自动补跑插件业务。
- 未完成媒体和转发归档可以恢复，恢复不重新触发插件或补发消息。
- 新版本初始化失败保持插件不可用，不自动回滚旧版本。
- 关闭和热重载采用协作式取消，不能强行卸载仍在执行的代码。

## 参考与许可证

插件化与事件传播设计参考 [AstrBot](https://github.com/AstrBotDevs/AstrBot)。OneBot 协议依据固定为 [标准提交 d4456ee](https://github.com/botuniverse/onebot-11/tree/d4456ee706f9ada9c2dfde56a2bcfc69752600e4)，实现扩展单独声明。

主要技术依据包括 [.NET 插件加载](https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support)、[程序集卸载](https://learn.microsoft.com/dotnet/standard/assembly/unloadability)、[EF Core SQLite](https://learn.microsoft.com/ef/core/providers/sqlite/) 与 [Channels](https://learn.microsoft.com/dotnet/core/extensions/channels)。许可证全文见 [LICENSE](LICENSE)。
