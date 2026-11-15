# 内存与索引速度探针

从仓库根运行，报告和测试索引放在忽略跟踪的 `work`，不读写正式历史、不启动 GUI、不读取原始磁盘卷。

```powershell
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/memory.json
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/live-memory.json --live
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/desktop-memory.json --desktop
dotnet run --project tools/QBox.MemoryProbe -c Release -- --indexing work/indexing-replay --remap
dotnet run --project tools/QBox.MemoryProbe -c Release -- --indexing work/repeated-replay --remap --repeated-names
dotnet run --project tools/QBox.MemoryProbe -c Release -- --pack work/frozen-indexes/C.qbi work/packed-indexes/C.qbi
dotnet run --project tools/QBox.MemoryProbe -c Release -- --pack-live work/source-indexes work/new-packed-indexes
dotnet run --project tools/QBox.MemoryProbe -c Release -- --compare-indexes work/frozen-indexes/C.qbi work/packed-indexes/C.qbi work/structure-C.json
```

前两项分别载入基础快照、基础加增量；`--desktop` 还载入当前机器的快捷方式和注册应用。报告包含前九项全部 SearchHit 字段的摘要、总命中、内核耗时、托管堆和进程内存；不输出真实文件路径。`QBOX_PROBE_QUERY_PASSES` 可将每组重复次数从默认 3 调整为 3～30。诊断探针会显式 GC 以区分存活数据与堆余量，产品没有强制 GC 或工作集裁剪。

`--pack` 只向独立路径写共享文本快照，保留几何、创建时间与检查点；禁止原地覆盖。它不复制或更新增量日志，测试实时来源时需同时复制相同基础身份的冻结 `.qbd`。它会强制重整已有 v2。`--compare-indexes` 先验证摘要及结构，再逐个节点、名称元数据和原始 UTF-16 核对，支持小于 2 GiB 的诊断快照；这一工具限制不是产品格式限制。

`--pack-live` 必须指定尚不存在的目标目录。它只读捕获各卷基础及完整增量批次，转换后核对所有节点/名称/原始 UTF-16、增量内容摘要、记录数及最终检查点；全部通过后才将临时目录发布为目标。不会修复源日志或覆盖已有目标，失败只清理本次临时目录。输出 `preparation.json` 记录策略及计数；各卷是分别捕获的视图，不是跨卷原子时刻。

标准构建将共享回指限制在 256 KiB 文本窗口；需要全局共享时，必须用 `-p:QBoxGlobalNameSharing=true` 编译探针，并使用独立 SDK 产物目录，避免污染标准 bin/obj。发布与全局探针命令见 [试用记录](../../docs/2026-11-15-全局共享试用版.md)。不能把标准模式执行 `--pack` 或 `--pack-live` 当成已做全局完全去重。

`--indexing` 使用相同的 201,001 条缓存记录和 2,000 条 USN 改名输入，执行实际 Build、Save、Load、增量读取/应用、日志追加和 Synchronize。`--remap` 将保存后重新映射的成本计入保存及合并。首轮保留作冷样本，后四轮比较中位数；每轮增量有 20 个相同批次样本，保留原始数据。这是实现层重放，不代表物理磁盘全量 MFT 吞吐。

设置 `QBOX_PROBE_INDEXING_COLLECT=1` 可在轮次之间显式回收上一轮对象，回收本身在阶段计时之外，用于定位 GC 对计时归属的影响；报告记录该选项。基线与候选必须使用相同选项，不能把这个控制条件当作产品自然驻留或实际合并尾延迟。

对照旧实现时先保存明确提交构建的 DLL，再让同一探针源文件引用那些冻结 DLL；不要重新构建引用当前源码的“旧版”项目。两边使用相同 SDK/运行时、冻结快照、增量和空历史，并核对文件哈希及所有结果摘要。
