# 可选的离线排序研究

本工具不属于产品运行或 `verify.ps1` 的依赖，不会把研究规则写入正式搜索器。当前排序规则研究已暂停。

需要研究时，用操作者自己的 `.qbi` 子集或完整索引及目标清单，不依赖任何作者个人目录。例如先执行开发指南中的 `scripts/dev.ps1 -PrepareOnly`，再运行：

```powershell
dotnet run --project tools/QBox.PriorityStudy -c Release -- work/dev/indexes docs/validation/listary-priority-defaults-7.1.0.47.json work/research-demo.json samples/research-targets.example.json
```

目标 JSON 是“查询 → 目标数组”。可提供 `name`、`target`、`isApplicationEntry`；同一条目标内提供的条件必须同时满足，至少提供名称或路径。`target` 可用 `%USERPROFILE%` 等环境变量。实际命中目标数量与数组长度不符时工具报错，不能把缺失目标当作通过。私人目标清单放在被忽略的 `work/` 下。

第五个参数可提供注册后缀快照，启用原生优先级模型。重放原始机器指令还需自行准备合法持有的原程序、Ghidra 和 JDK；历史证据见 [研究资料](../../docs/README.md)。普通开发不需要安装这些软件。
