# 纯 Rust 重写：执行材料入口

更新于 2026-11-18。**正式替换阶段从 [Windows 纯 Rust 正式替换实施计划](../plans/2026-11-18-qbox-rust-windows-parity.md) 接续。** 现有试用版已交付真实卷搜索、打开和手动同步，[试用版调整方案](WINDOWS-TRIAL.md) 记录当时的交付范围。

## 正式替换阶段的参考材料

| 材料 | 作用 |
|---|---|
| [PLAN.md](PLAN.md) | 原总纲，保留原有范围、阶段与验收要求 |
| [DECISIONS.md](DECISIONS.md) | 决策、总纲的明确化、必须先验证的假设 |
| [DESIGN.md](DESIGN.md) | 数据模型、接口、线程、查询/UI 状态、NTFS 与平台实现 |
| [STORAGE.md](STORAGE.md) | 基础文件、增量、manifest、提交与崩溃恢复协议 |
| [WORKER-PROTOCOL.md](WORKER-PROTOCOL.md) | GUI/管理员 worker 的通信、安全、租约与退出 |
| [MIGRATION.md](MIGRATION.md) | 源文件迁移表、按依赖排序的实施任务及提交边界 |
| [VALIDATION.md](VALIDATION.md) | 功能对照、指标定义、统计判定、脚本及报告契约 |

正式替换的任务顺序和验证要求按实施计划执行。MIGRATION 保留原始映射，VALIDATION 用于专项排查。现成的基线和验证结果见实施工作树的 PROGRESS.md。批量统计矩阵和 8 小时稳定性观察按发现的问题选用。

## 文档与实现的区别

原设计记录的是当时的技术决策。实现进度以实施工作树的源码和 PROGRESS.md 为准。用于对照的产品基线是 `codex/feat-new@1f06daa0c9a9f29d8acbce357d169485afda92a4`。

协议和行为约束按本文档实施；普通内部函数拆分可由执行者决定。GUI 原型、权限集成和性能验证有明确前置门槛，失败时记录证据，不把待验证假设写为已通过。对于发现的真实基线缺陷，建立 characterization 检查并单独说明，不为追求对照通过复制内存安全或数据损坏缺陷。

历史设计保留在 PLAN 和 DECISIONS。正式替换按实施计划的短对照取证，搜索速度不降级仍是要求。

试用阶段在实施分支现有 PROGRESS.md 追加本轮交付记录即可。正式数据覆盖、正式发布、合并及推送仍需另行安排。
