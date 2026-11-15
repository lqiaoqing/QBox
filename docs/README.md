# 文档目录

开发从 [DEVELOPING](../DEVELOPING.md) 开始，当前任务和边界见 [ROADMAP](../ROADMAP.md)。以下设计与研究报告保留历史语境，不作为当前开发步骤或授权。

真实机器报告中的个人目录、工作根路径和 SID 已脱敏。`%USERPROFILE%`、`%WORK_ROOT%`、`%QBOX_REPO%`、`%REA_ROOT%` 代表原机器路径占位符，不是仓库依赖。压缩指令 oracle 保持原样；脱敏报告中的原始输入哈希只作溯源，不能视为脱敏后文件的内容哈希。

面向使用和构建的说明在仓库根目录的 [README.md](../README.md) 和 [CHANGELOG.md](../CHANGELOG.md)。第三方读音表许可见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 发布与验收

- [2026-11-15 内存优化记录](2026-11-15-内存优化.md)：0.2.2 共享索引、路径与应用目录分配；包含索引速度重放边界。
- [2026-11-15 Listary 内存架构对照](2026-11-15-Listary内存架构对照.md)：当前原生后端、查询生命周期、内存指标边界及 QBox 后续优化候选；研究未改产品行为。
- [2026-11-14 搜索补全实施记录](2026-11-14-搜索补全/TASK.md)：0.2.0 实时更新、文件/目录拼音与 0.2.1 中文查询延迟修复。
- [窗口版验证](desktop-validation-2026-11-13.md)
- [索引验证](index-validation-2026-11-13.md)
- [0.1.2 应用检索修复](application-validation-2026-11-14.md)
- [0.1.3 应用全拼增量](application-pinyin-validation-2026-11-14.md)
- [搜索验证](search-validation-2026-11-14.md)

## 历史计划（非当前待办）

- [桌面设计](plans/2026-11-13-desktop-design.md)
- [搜索设计](plans/2026-11-13-search-design.md)
- [搜索实施清单](plans/2026-11-13-search-implementation.md)
- [应用全拼计划](plans/2026-11-14-application-pinyin.md)
- [应用召回修复计划](plans/2026-11-14-application-recall-fix.md)

## 排序与来源研究

这些是离线对照和证据，未全部接入正式版规则。

- [优先级默认表对照](priority-study-2026-11-14.md)
- [优先级计算](priority-computation-2026-11-14.md)
- [优先级编码与注册后缀](priority-codec-and-suffixes-2026-11-14.md)
- [结果键桥接](result-key-bridge-2026-11-14.md)
- [原生优先级键](native-priority-study-2026-11-14.md)
- [上游候选选择与来源](upstream-selection-and-sources-2026-11-14.md)
- [Listary 优先级证据](listary-priority-evidence-2026-11-14.md)
- [NTFS 格式来源](ntfs-format-sources.md)

原始对照数据在 [validation](validation/)，示意图在 [references](references/)。
