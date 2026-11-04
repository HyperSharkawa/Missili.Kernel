# 第三方来源与许可

本项目采用 AGPL-3.0；依赖、译写及复制内容各自保留其适用许可。以下设计参考没有直接复制源代码：

| 来源 | 固定参考 | 本项目使用位置 |
| --- | --- | --- |
| AstrBot | [配置路由与管理，0cd42bd4d](https://github.com/AstrBotDevs/AstrBot/blob/0cd42bd4d/astrbot/core/umop_config_router.py) | `ModelConfigurationService`：独立配置档、会话首匹配路由 |
| AstrBot | [媒体工具，f926af9](https://github.com/AstrBotDevs/AstrBot/blob/f926af99ec5f8bd85b64413f8bfad55da76ef73b/astrbot/core/utils/media_utils.py) | 媒体准备和业务调用分层；图片算法使用 ImageSharp |
| MaiBot | [模型配置，c642954](https://github.com/Mai-with-u/MaiBot/blob/c6429547af711555e3d4d1b8d04b413572cb616c/src/config/model_configs.py) | 按用途配置候选模型，模型与回复决策分开 |
| Codex CLI | [代理控制，d5830f6](https://github.com/openai/codex/blob/d5830f6fc64ee594ed63a97d9ac968bba87033fa/codex-rs/core/src/agent/control.rs) | 调用归属、取消与实际退出、保留结构化部分结果；尚未实现代理编排 |

实际运行依赖：

- [OpenAI .NET 2.14.0](https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0)，MIT，用于 Chat Completions、Responses 和语音转写提供商。
- [Anthropic C# 13.4.0](https://github.com/anthropics/anthropic-sdk-csharp/tree/Anthropic-v13.4.0)，Apache-2.0，用于 Anthropic Messages 提供商。
- [ImageSharp v4.1.3](https://github.com/SixLabors/ImageSharp/tree/v4.1.3)，按上游 Split License 对本开源项目提供的 Apache-2.0 条件使用。仓库内源码构建包为 [Missili.ThirdParty.ImageSharp](../third-party/nuget/Missili.ThirdParty.ImageSharp.4.1.3-missili.2.nupkg)，包内保留上游许可、Apache-2.0 全文与第三方声明。[来源与修改说明](../third-party/imagesharp/MISSILI-BUILD.md)及实际补丁位于 `third-party/imagesharp`，修改包含构建校验移除及 APNG 零分母帧时长修正。

协议与平台依据：

- [OpenAI 流式响应](https://developers.openai.com/api/docs/guides/streaming-responses)：终止事件、增量与错误分别处理。
- [Anthropic 原生思考块与工具续接](https://platform.claude.com/docs/en/build-with-claude/thinking-tool-workflows)：原生块和签名保持来源及顺序。
- [PNG 第三版 fcTL](https://www.w3.org/TR/png/#11fcTL)：零分母按 100 解释；零时长采样规则由本项目明确配置语义。
- [OneBot get_record](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/api/public.md#get_record-获取语音)：适配器提供 WAV 派生，模型模块不直接实现 OneBot 网络请求。

协议 Mock 的合成 JSON 与 SSE 夹具直接按规范编写，没有调用生产编码器生成预期结果。
