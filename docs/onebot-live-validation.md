# OneBot 真实联调记录

## 本轮证据

本轮使用两个已登录的真实 QQ 账号，用户授权的互相私聊及一个专用测试群。所有发送入口检查目标白名单，撤回仅针对本轮自己发送的合成消息。Token 通过隐藏控制台输入，不写入源码、配置文件、报告或项目记忆。

被测 Missili 适配器连接 SnowLuma **2.4.0-node**，独立测试对端连接 SnowLuma **2.0.2-node**；两端均报告 OneBot `v11`。实际连接为正向 Universal WebSocket。反向与分离布局的既有 Mock 验收不能等同于本轮真实平台验收。

共 **18 个功能场景通过**：

| 范围 | 实际通过的检查 |
| --- | --- |
| 基础收发 | 私聊入站及插件回复、私聊出站确认、群入站及插件回复、群文字／表情／提及发送 |
| 图片 | 入站原件归档、重复内容去重、流导入及发送 |
| 语音 | 入站原件归档、流导入及发送 |
| 引用 | 引用回复及本地读取、远端补取且不重新分发为聊天 |
| 转发 | 普通转发归档、标准节点发送、引用已有消息构造的嵌套转发及内部图片归档 |
| 视频 | 入站原件归档、流导入及发送 |
| 生命周期与撤回 | 适配器热重载后继续收发、群消息撤回保留原文并更新标记 |

核对使用真实生产适配器、SQLite 历史、媒体服务和编译探针插件。发送成功同时检查回执与对端实际事件，不仅检查 API 的 `status`。

## 私聊撤回尚未通过：平台消息身份不一致

受控私聊测试中，SnowLuma 上报了 `friend_recall`，但其中 `message_id` 与原消息先前上报的标识不同。通知中的账号及好友身份类型正常，事件也已保存在 Missili 的平台事件历史；无法准确关联的原消息保留未撤回状态。

对同一条合成消息进一步调用被测端 `get_msg`：

- 使用原消息 ID：`status=ok`、`retcode=0`，返回本轮测试正文。
- 使用撤回通知 ID：`status=failed`、`retcode=100`。

下例替换了账号、消息标识与时间数值，仅保留实测的不一致关系：

```json
{"post_type":"message","message_type":"private","self_id":10001,"user_id":20002,"message_id":101,"message":[{"type":"text","data":{"text":"合成测试消息"}}]}
{"post_type":"notice","notice_type":"friend_recall","self_id":10001,"user_id":20002,"message_id":202}
```

OneBot 标准规定 `friend_recall.message_id` 为被撤回消息的 ID。这是当前受测实现输出的身份不一致，不能以时间接近或正文相似代替精确关联。Missili 继续保留原文和独立撤回事实，不修改为猜测匹配。新增标准回归用例分别证明正确 ID 会更新标记、错误 ID 不会误标其他消息。

## 嵌套转发的发送形式差异

将普通文字、图片和一个内嵌 `node` 混在同一节点的 `content` 中发送时，测试对端返回 `status=failed`、`retcode=1400`。本轮不将该形式记为通过，也不自动拆分成多条发送。

改用标准定义的引用节点 `{"type":"node","data":{"id":"已有消息 ID"}}` 引用已发送的子转发后，父转发、内层正文和其中图片均成功归档。该结果证明嵌套展开链路可用，并不证明前一种直接混合形式被实现支持。

## 依据与后续

- [OneBot v11 通知定义](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/event/notice.md)。
- [OneBot v11 消息段与引用转发节点](https://github.com/botuniverse/onebot-11/blob/d4456ee706f9ada9c2dfde56a2bcfc69752600e4/message/segment.md)。
- [SnowLuma v2.4.0 撤回转换](https://github.com/SnowLuma/SnowLuma/blob/v2.4.0/packages/onebot/src/event-converter/to-notice.ts)及[事件关联流程](https://github.com/SnowLuma/SnowLuma/blob/v2.4.0/packages/onebot/src/event-pipeline.ts)。
- `OneBotRecallIdentityTests`：准确平台标识的正反两个回归用例。

私聊撤回身份问题需在 SnowLuma 侧进一步修复或解释，再用相同场景复验。本轮没有升级或修改两边 SnowLuma，也没有向上游发布 Issue。
