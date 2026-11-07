# 默认审批政策的来源与修改

`Missili.Kernel/ApprovalPrompts.cs` 的默认政策依据 OpenAI Codex 提示词翻译、删改，并增加 Bot 用户身份、同群补充、子代理来源和 local shell 的适用规则。

- 固定提交：`2e58636d16794a150e562cc34b63ca3035494e50`。
- 来源：[policy_template.md](https://github.com/openai/codex/blob/2e58636d16794a150e562cc34b63ca3035494e50/codex-rs/prompts/templates/guardian/policy_template.md)、[policy.md](https://github.com/openai/codex/blob/2e58636d16794a150e562cc34b63ca3035494e50/codex-rs/prompts/templates/guardian/policy.md)。
- 上游版权：Copyright 2025 OpenAI；适用 Apache License 2.0，全文见 LICENSE。
- NOTICE 保留与审批提示词有关的 Codex 归属；没有采用 Ratatui 代码。
- 改写使用二元 `allow`／`deny` 与中文意见，移除上游专属执行环境假设。它不是上游原文的完整译本。

框架职责与输出契约固定；默认业务政策可通过配置选择或由完整自定义政策替换。
