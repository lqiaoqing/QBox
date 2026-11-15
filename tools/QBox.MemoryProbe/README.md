# 内存与索引速度探针

从仓库根运行，报告和测试索引放在忽略跟踪的 `work`，不读写正式历史、不启动 GUI、不读取原始磁盘卷。

```powershell
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/memory.json
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/live-memory.json --live
dotnet run --project tools/QBox.MemoryProbe -c Release -- work/frozen-indexes work/desktop-memory.json --desktop
dotnet run --project tools/QBox.MemoryProbe -c Release -- --indexing work/indexing-replay --remap
```

前两项分别载入基础快照、基础加增量；`--desktop` 还载入当前机器的快捷方式和注册应用。报告包含全部 SearchHit 字段的摘要、总命中、内核耗时、托管堆和进程内存；不输出真实文件路径。诊断探针会显式 GC 以区分存活数据与堆余量，产品没有强制 GC 或工作集裁剪。

`--indexing` 使用相同的 201,001 条缓存记录和 2,000 条 USN 改名输入，执行实际 Build、Save、Load、增量读取/应用、日志追加和 Synchronize。`--remap` 将保存后重新映射的成本计入保存及合并。首轮保留作冷样本，后四轮比较中位数；每轮增量有 20 个相同批次样本，保留原始数据。这是实现层重放，不代表物理磁盘全量 MFT 吞吐。

对照旧实现时先保存明确提交构建的 DLL，再让同一探针源文件引用那些冻结 DLL；不要重新构建引用当前源码的“旧版”项目。两边使用相同 SDK/运行时、冻结快照、增量和空历史，并核对文件哈希及所有结果摘要。
