# QBox Rust 搜索引擎校验

日期：2026-11-16（Asia/Shanghai）
工作树：`D:\Work\workspace\QBox-rust-engine`
分支：`feat/rust-engine-validation`
基线提交：`75b9a9275daf2d3d900d64c3308fb06d015f67c3`（`release: publish QBox 0.2.5`）
原始仓库 `D:\Work\workspace\QBox` 仍在 `codex/feat-new`，未改主干工作区，未 push。

## 工具链（提醒）

- rustup 1.29.3，rustc / cargo 1.100.0，默认 `stable-x86_64-pc-windows-gnu`
- 安装位置：`C:\Users\zsj\.cargo`、`C:\Users\zsj\.rustup`
- 必须使用 rust-mingw 自带链接器（`gnu-linker.cmd` + `link-self-contained=yes`）。系统 MinGW-w64 gcc 8.1 链接出的 exe 会在本机 Windows 11 26200 上因 `kernel32.dll` 不再导出 `WaitOnAddress` 而以 `0xC0000139` 启动失败。
- 构建产物：`work/rust-target`

## 对照结果

运行：`cargo run --bin engine-harness` 或 `cargo test --lib`。

| 套件 | 结果 |
| --- | --- |
| ASCII FuzzyMatcher（`ascii-*.json`，含 holdout） | **685/685 通过** |
| `PinyinChecks`（含 5 组收集器集成） | **36/36 通过** |
| `LauncherOrderingChecks`（2048 组 `CompareNative` + 10 组收集器/分桶） | **2058/2058 通过** |

失败明细：`work/rust-validation/engine-failures.txt`（`pinyin_failed=0 ordering_failed=0`）。

拼音读音表直接读取工作树中的 `src/QBox.Core/Matching/Data/pinyin.tsv`，未改写。扩展首字母/半拼、完整音节边界、多音字（重庆）、`吕`/`𠀀`、未收录字符 `𠮷`、已取消 token、字面分优先、补充缩写不挤占字面首屏，以及快捷方式归并高亮均与 C# 一致。收集器未并行化扫描；序号与分桶次序与 C# 行为检查一致。

上表仍是匹配器 / 收集器对照，不是实盘结论。下面是同一份复制索引上的只读 `.qbi` 公平首屏对比；本次仍不做产品 go / no-go。

## 复制的只读索引

- 目的目录：`D:\Work\workspace\QBox-rust-engine\work\rust-validation\indexes`（工作树自己的 state，`work/` 已被 gitignore）
- 来源：正式版正在使用的 `D:\Work\workspace\QBox\work\indexes\{C,D}.qbi`。复制时以共享读打开（`FileShare.ReadWrite|Delete`），未写入、未抢锁、未打开源文件的写句柄。未复制 `.qbd` / `.lock`：本轮公平对比只用静态 `.qbi`。
- 试用目录里的 `{C,D}.qbi` 大小与时间戳和正式版相同，未再拷一份。正式版与试用目录的 `.qbd` 不同，也未使用。
- `C.qbi`：212,915,109 字节，SHA-256 `DF1D6D91FF7807A3EC3FE734C12AC8D9522921CDEBA0FEBB2959F9CB8F43953B`
- `D.qbi`：77,979,759 字节，SHA-256 `BE37E4900DF29186BBDAAEE1DD1C03FE75AD015A4F79411150BEEFADDA4421C7`

禁止写入的路径均未改动：`D:\Work\workspace\QBox\work\indexes`、`%LOCALAPPDATA%\QBox`、`D:\Work\workspace\QBox-first-paint\work\first-paint-state`。

## 只读 walker

Rust 实现在 `src/qbi.rs`，布局按 `VolumeIndexStore.cs` / `MappedIndexStorage.cs` / `VolumeIndex.cs` / `NamePresenceIndex.cs` / `PhoneticNameIndex.cs` 移植，只做 mmap 读取，不写回文件。

- 魔数 `QBXNTFS1`，格式版本 2，正文 SHA-256 与文件尾 32 字节一致。
- 卷覆盖与 C# `VolumeIndexStore.Load` 一致：
  - `C:\` serial `20c4668ac4666250`，files/nodes=1,994,298，names=3,442,511
  - `D:\` serial `182aac012aabda50`，files/nodes=1,016,618，names=1,066,239
- 按卷依次 `SearchInto`（先 C 后 D，路径序 `OrdinalIgnoreCase`），未并行化卷扫描，收集器序号与平局规则不变。
- 本轮不计应用、快捷方式和 `.qbd` 实时覆盖，只走静态索引路径。

## 公平首屏对比

同一台机器、同一份复制索引。C# 用工作树内薄宿主 `tools/QBox.EngineBench`（`VolumeIndexStore.Load` + `PrepareSearch` + `SearchInto` + `Finish`，空 `SelectionHistory`，limit 200），不经过 WPF UI，也未改产品匹配 / 排序代码。Rust 用 `cargo build --release --bin index-bench`，同一协议。

计时口径：每个查询 1 次预热 + 5 次计时；每次计时包含全部卷的 presence 筛选、CanMatch / ScoreMatched、收集器插入和 `Finish` 物化。不含进程启动、mmap/加载、SHA-256、`PrepareSearch` 和预热。C# 用 `Stopwatch`，Rust 用 `std::time::Instant`。旧 UI 墙钟仅作参考，不能当 C# 基线：`1` 2336.7 ms / 1,406,752 shown 200；`豆包` 324.8 ms / 6；`CODE` 1007.1 ms / 232,713 shown 200；`python` 289.1 ms / 104,992 shown 200。引擎路径总数与 UI 略有差别是因为 UI 还计入应用、快捷方式和 live `.qbd`。

加载 / 准备（不计入查询计时）：

| 卷 | C# load_ms | C# prepare_ms | Rust load_ms | Rust prepare_ms |
| --- | ---: | ---: | ---: | ---: |
| C.qbi | 498 | 937 | 173 | 1131 |
| D.qbi | 200 | 220 | 66 | 341 |

查询结果（加速比 = C# / Rust，大于 1 表示 Rust 更快）：

| 查询 | 命中 C# | 命中 Rust | 首屏集合与次序 | C# min / median (ms) | Rust min / median (ms) | 加速比 min / median |
| --- | ---: | ---: | --- | ---: | ---: | ---: |
| `1` | 1,406,557 shown 200 | 1,406,557 shown 200 | 一致（200 条 id/name/target/kind/score/group/application/direct 全同） | 130.894 / 135.401 | 294.041 / 301.754 | 0.445× / 0.449× |
| `豆包` | 6 shown 6 | 6 shown 6 | 一致（6 条全同） | 0.273 / 0.292 | 0.099 / 0.105 | 2.745× / 2.785× |
| `CODE` | 232,676 shown 200 | 232,676 shown 200 | 一致（200 条全同） | 166.906 / 174.355 | 146.326 / 233.650 | 1.141× / 0.746× |
| `python` | 104,992 shown 200 | 104,992 shown 200 | 一致（200 条全同） | 46.320 / 47.015 | 64.513 / 71.677 | 0.718× / 0.656× |

C# 五次：`1` [143.206, 133.292, 135.401, 130.894, 215.964]；`豆包` [0.406, 0.292, 0.283, 0.303, 0.273]；`CODE` [174.355, 166.906, 206.979, 180.783, 171.779]；`python` [46.815, 46.320, 47.987, 50.428, 47.015]。
Rust 五次：`1` [306.161, 294.041, 295.239, 301.754, 302.567]；`豆包` [0.101, 0.105, 0.099, 0.105, 0.109]；`CODE` [146.326, 236.526, 230.938, 243.884, 233.650]；`python` [64.513, 71.677, 72.170, 104.564, 65.722]。

首屏正确性：四个查询的命中总数与首屏次序均与 C# 引擎路径一致，没有错位。耗时方面，两端在 `1` 上都低于 1 秒，但 Rust 中位时约为 C# 的 2.2 倍，未达到「明确快 30%」的对照标准。`豆包` 的 Rust 更快，但只有约 0.2 ms，样本太小。`CODE` 的最好一次略快、中位更慢；`python` 整体更慢。这份对照还不能说明产品层替换会更快。

原始仓库 `D:\Work\workspace\QBox` 仍在 `codex/feat-new`（`75b9a9275daf2d3d900d64c3308fb06d015f67c3`），工作区干净。未启动或停止正式版 0.2.4 / 0.2.5，未结束 first-paint 试用进程，未 push。

## 热路径优化后再测（去掉 UTF-16 到 String 的拷贝）

优化只动 Rust 引擎热路径，不改 C# 产品匹配 / 排序，卷扫描仍是先 C 后 D、不并行。`cargo test --lib` 仍为 ASCII **685/685**、拼音 **36/36**、收集器/排序 **2058/2058**。

主要改动：

- `CanMatch` / `ScoreMatched` 直接走 `&[u16]`。ASCII 单字符用按 4 个 UTF-16 单元一组的 SWAR，等价于 C# `IndexOf` / `IndexOfAny`，不再先 `iter().all()`，也不为候选项分配 `Vec<u8>` 或 `utf16_to_string`。
- 收集器探测 `would_enter` 只比较分桶键，不分配名字；`utf16_to_string` 只在该行真正进入前 N 时发生（随后 `Finish` 再物化路径）。
- 各卷共用收集器里的那一份 `PreparedQuery`，不再对每个卷 `to_string` + `PreparedQuery::new`，也不再对每个可执行文件 `to_ascii_lowercase`。

同一份复制索引、同一台机器、同一口径（1 次预热 + 5 次计时，只计全部卷 `SearchInto` + `Finish`）。C# 基线仍是 `tools/QBox.EngineBench`，不用旧 UI 墙钟。

加载 / 准备（不计入查询计时）：

| 卷 | C# load_ms | C# prepare_ms | Rust load_ms | Rust prepare_ms |
| --- | ---: | ---: | ---: | ---: |
| C.qbi | 665 | 991 | 177 | 1206 |
| D.qbi | 206 | 226 | 64 | 331 |

查询结果（加速比 = C# / Rust，大于 1 表示 Rust 更快）：

| 查询 | 命中 C# | 命中 Rust | 首屏集合与次序 | C# min / median (ms) | Rust min / median (ms) | 加速比 min / median |
| --- | ---: | ---: | --- | ---: | ---: | ---: |
| `1` | 1,406,557 shown 200 | 1,406,557 shown 200 | 一致（200 条全同） | 157.082 / 217.718 | 83.082 / 85.489 | 1.891× / 2.547× |
| `豆包` | 6 shown 6 | 6 shown 6 | 一致（6 条全同） | 0.277 / 0.292 | 0.095 / 0.097 | 2.908× / 3.006× |
| `CODE` | 232,676 shown 200 | 232,676 shown 200 | 一致（200 条全同） | 168.338 / 170.226 | 92.992 / 94.684 | 1.810× / 1.798× |
| `python` | 104,992 shown 200 | 104,992 shown 200 | 一致（200 条全同） | 44.262 / 44.942 | 22.287 / 25.198 | 1.986× / 1.784× |

C# 五次：`1` [157.082, 171.274, 256.283, 217.718, 225.906]；`豆包` [0.402, 0.295, 0.292, 0.286, 0.277]；`CODE` [168.338, 170.226, 212.261, 168.841, 171.806]；`python` [45.905, 44.851, 44.942, 44.262, 46.403]。
Rust 五次：`1` [94.107, 84.969, 83.082, 85.489, 97.424]；`豆包` [0.097, 0.095, 0.097, 0.099, 0.100]；`CODE` [98.052, 94.684, 92.992, 93.428, 95.299]；`python` [25.198, 23.343, 22.287, 31.015, 32.307]。

成功标准是 `1` / `CODE` / `python` 的 Rust 中位时至少比 C# 快 30%（加速比 ≥ 1.30×），且首屏仍然一致。本轮三个查询的中位加速比为 2.547× / 1.798× / 1.784×，**达到了该标准**；四个查询的命中总数与首屏次序均一致。需要说明：本次 C# 的 `1` 波动较大（157–256 ms，中位 217.718 ms），上一轮未优化对照里同一 C# 宿主中位约 135.401 ms。即便用上一轮更紧的 C# 中位来比本次 Rust（`1` 85.489、`CODE` 94.684、`python` 25.198），加速比仍约为 1.58× / 1.84× / 1.87×，同样超过 30%。

## 实施前复核（2026-11-16，阶段 0）

- 基线确认：HEAD `9125abf`，工作区干净；`cargo test --lib` 与 `cargo run --release --bin engine-harness` 实跑通过，ASCII 685/685、拼音 36/36、排序收集 2058/2058。release 二进制退出码为 0，没有 `0xC0000139`。
- 链接器仍由 `.cargo/config.toml` 指向 `gnu-linker.cmd`，后者使用 rustup sysroot 下 self-contained 的 `x86_64-w64-mingw32-gcc.exe`，`link-self-contained=yes` 保持不变。
- GUI 搜索顺序（`MainWindow.SearchAsync`，后台线程）：`SearchCollector(query, limit, token)` → `PackagedApplicationCatalog.SearchInto` → `ShortcutCatalog.SearchInto` → `ReplacedIds` → `MatchedExecutableTargets` → 按已加载卷顺序对每个 `LiveVolumeIndex.SearchInto` → `Finish`。CLI `search` / `open` 走同一顺序。
- 每个 `LiveVolumeIndex.SearchInto` 先 `Base.SearchInto`（存在变更目录时用 live 路径解析），再 `_overlay.SearchInto`（总是解析 live 路径）。卷顺序按 `.qbi` 文件名 Ordinal（当前 C 再 D），禁止并行扫卷。
- 单个 `VolumeIndex.SearchInto` 先按 presence 索引筛字面候选：`.lnk`/`.exe` 去扩展名可匹配时优先展示友好名，否则回退完整名；DOS 8.3 别名可命中但不重复占位；EXE 在非包目录且分值 >1 时作为直接应用入口，`representedExecutables` 中的目标不再标记。随后跑拼音补充命中，与字面入口互斥，使用 supplemental 桶；查询含 `.lnk` 时跳过快捷方式来源与替换逻辑。
- 收集器保持 7 个 lane、动态容量和 `Mixed` 前十规则；总命中数在过滤后即计数，即使 `limit == 0` 也继续统计。索引身份为 `ntfs:{serial:x16}:{file:x16}:{parent:x16}:{sha256(utf16 name)}`，历史信号按 `(file,parent,hash)` 关联。
- 后续接入 Rust 时必须保持上述来源顺序、卷顺序、路径解析与历史桶规则不变；不得为提速并行扫卷或提前截断语料。

## 阶段 1：live `.qbd` + 应用 + 快捷方式（2026-11-16）

Rust 新增 `live.rs`（QBOXDLT1 帧读取、base+overlay、目录可达/路径解析）与 `pipeline.rs`（应用→快捷方式→各卷 base+overlay）。应用与快捷方式仍由 C# 采集：`EngineCatalogSnapshot` 导出最终条目，`EngineHistoryBlob` 导出每查询历史快照；Rust 只复现检索、替换与排序。

对照数据（只读复制到 `work/rust-validation/live-indexes`，共享读源目录，未写源目录）：

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `C.qbi` | 212,915,109 | `df1d6d91ff7807a3ec3fe734c12ac8d9522921cdeba0febb2959f9cb8f43953b` |
| `D.qbi` | 77,979,759 | `be37e4900df29186bbdaaee1dd1c03fe75ad015a4f79411150beefadda4421c7` |
| `C.qbd` | 2,513,609 | `975b8228d15d170503391dbb086f685998454b364c5380d81d911d1f485457c9` |
| `D.qbd` | 344,658 | `6c8c57da364ca1389ab1ff71293ae23ed8811fc57791671a4df4117dff40b29d` |

C# 宿主 `tools/QBox.EngineBench` 增加 `--live`、`--catalogs`、`--catalog-out`、`--history`、`--history-set-out`；应用 46 条、快捷方式 222 组，历史使用本机 `%LOCALAPPDATA%\QBox\history.json`（只读）。协议仍为 1 次预热 + 5 次计时，计时含应用/快捷方式检索、`ReplacedIds`、`MatchedExecutableTargets`、全部卷 `SearchInto` 与 `Finish`。

当前静态 `.qbi`（同一复制索引，空历史）：

| 查询 | 命中 C# / Rust | 首屏 | C# min / median (ms) | Rust min / median (ms) | 加速比 median |
| --- | --- | --- | ---: | ---: | ---: |
| `1` | 1,406,557 / 1,406,557 | 一致 | 130.128 / 132.208 | 84.988 / 85.047 | 1.555× |
| `豆包` | 6 / 6 | 一致 | 0.281 / 0.289 | 0.098 / 0.099 | 2.919× |
| `CODE` | 232,676 / 232,676 | 一致 | 171.976 / 181.605 | 82.962 / 88.664 | 2.048× |
| `python` | 104,992 / 104,992 | 一致 | 45.649 / 47.644 | 21.731 / 22.783 | 2.091× |

live `.qbd` + 应用 + 快捷方式 + 真实历史：

| 查询 | 命中 C# / Rust | 首屏 | C# min / median (ms) | Rust min / median (ms) | 加速比 median |
| --- | --- | --- | ---: | ---: | ---: |
| `1` | 1,407,763 / 1,407,763 | 一致 | 361.187 / 376.492 | 262.341 / 265.896 | 1.416× |
| `豆包` | 6 / 6 | 一致 | 0.688 / 0.754 | 0.716 / 0.767 | 0.984× |
| `CODE` | 232,738 / 232,738 | 一致 | 185.861 / 190.965 | 172.028 / 192.240 | 0.993× |
| `python` | 104,992 / 104,992 | 一致 | 54.114 / 54.647 | 56.822 / 58.718 | 0.931× |

首屏一致指前 200 条（不足则全部）的 `id/name/target/kind/score/group/application/direct` 与次序完全相同；四个查询的命中总数也相同。`C:\` 3223 条变更、`D:\` 1363 条变更，两个卷都存在目录变更，base 搜索按 C# 口径启用 live 路径解析。

结论：静态路径仍满足 `1` / `CODE` / `python` 中位 ≥30% 加速（对照更严的 `1` 135.401 ms 基线，本轮 Rust 85.047 ms 仍约 1.59×）。live 路径下 `1` 仍约 1.4×，但 `CODE` / `python` 因每个候选的目录可达性与路径解析只与 C# 打平，未达到 30% 目标；这是 overlay 复杂度导致的真实结果，未改口径。后续接入 FFI/UI 时继续报告这条差异。

`cargo test --lib` 现为 6 项：原有 3 项（685/685、36/36、2058/2058）加历史 blob、忽略大小写集合、内存卷镜像往返 3 项。

## 阶段 2：FFI sidecar 与 C# 包装（2026-11-16）

`qbox_engine` 现在同时产出 `rlib` 与 `qbox_engine.dll`（cdylib）。C ABI：`qbox_engine_open` / `prepare` / `refresh` / `search` / `cancel` / `last_error` / `free_buffer` / `close`。索引在 `open` 时加载并完成 PrepareSearch；`search` 接收 UTF-16 查询、历史 blob 与请求号，返回紧凑二进制结果（魔数 `QBXRES01`，v1；字段含候选、分值、高亮、分组与历史 ticks）。取消使用只增的请求阈值，旧查询被取消但不会误伤之后的新查询。

- `qbox_engine.dll` 用现有 rust-mingw 自包含链接器构建；`NativeLibrary.Load` 在本机成功，未出现 `0xC0000139`。
- C# 包装 `src/QBox.Windows/Search/RustSearchEngine.cs`：LibraryImport、DLL 解析（exe 同级 / `QBOX_ENGINE_DLL` / 开发输出目录）、结果解析、取消注册、刷新等待活跃查询结束；失败时抛出可捕获异常，由调用方回退 C# 路径。
- 应用/快捷方式仍由 C# 加载，`EngineCatalogSnapshot` 快照传给 Rust；历史由 `EngineHistoryBlob` 按查询序列化。
- `tools/QBox.EngineBench` 增加 `--engine rust`、`--cancel-test`、`--fallback-test`。取消测试通过；无效索引目录打开失败并由调用方捕获（回退路径）。四个查询的 FFI 首屏与 C# 引擎路径完全一致，命中总数相同。

FFI live 路径（1 预热 + 5 次计时，含历史 blob 序列化与结果解析）：

| 查询 | C# median (ms) | Rust FFI median (ms) | 加速比 |
| --- | ---: | ---: | ---: |
| `1` | 376.492 | 404.942 | 0.930× |
| `豆包` | 0.754 | 0.810 | 0.931× |
| `CODE` | 190.965 | 184.793 | 1.033× |
| `python` | 54.647 | 56.223 | 0.972× |

FFI 的大结果（`1`）比直接引擎路径慢，主要来自跨 ABI 的结果/历史序列化与解析；首屏正确性已对齐。阶段 3 的 UI 试跑使用该 FFI 路径，并在报告中保留此开销说明。

`scripts/publish-desktop.ps1` 现在先构建 `qbox_engine.dll`，再复制到发布目录与 `QBox.exe` 同级；单文件发布不打包 sidecar。

## 阶段 3：Desktop/CLI 接入与独立包试跑（2026-11-16）

- `DesktopOptions` 新增 `--search-engine csharp|rust`，默认 `csharp`。`MainWindow` 在 `ReloadAsync` 中打开/刷新 sidecar，搜索走 Rust；加载或运行失败会记录日志并回退 C#。`MonitorIndexesAsync` 与 `RefreshCatalogsAsync` 会刷新 Rust 的 `.qbd`/目录快照；`Quit` 释放句柄。
- CLI `search`/`open` 支持 `--search-engine rust`（需要 `--index-directory`，或目录中只有一个 `.qbi`），失败时回退 C#。
- `publish-desktop.ps1` 构建 `qbox_engine.dll` 并复制到 `QBox.exe` 同级；独立包位于 `work/trial-package`。
- 试用 state：`work/trial-state`，索引为 `work/trial-state/indexes` 的副本；`settings.json` 关闭自动更新。未写 `D:\Work\workspace\QBox\work\indexes`、`%LOCALAPPDATA%\QBox` 或 first-paint state。

实机结果：

- GUI 进程 TokenElevation=0（未提权），进程中已加载 `qbox_engine.dll`。
- 触发“更新索引”后 worker 进程 TokenElevation=1，状态 `Watching`；D 卷正常“实时更新中”。C 卷报告 `稍后重试，保留旧索引：Access to the path is denied.`，发生在基础索引 compact/替换期间；尚未隔离是否由 Rust mmap 与 GUI 同时映射导致，也可能是既有行为，合并前需复查。
- UI 查询 `1`、`豆包`、`CODE`、`python` 均有结果。Rust 与强制 C# 两个试用实例的 `CODE`、`1`、`豆包` 首屏在 UIA 树中逐字一致。
- 单击结果打开 `豆包示例.txt` 并隐藏 QBox；重新唤出后按 Enter 打开同一结果；Esc 隐藏并清空查询，重新唤出为空查询。
- 隐藏后工作集从 233,562,112 降到 13,193,216 字节（空闲回收生效；第二次从约 396 MB 降到约 52 MB）。
- 完整界面查询 `1` 状态为 `1,435,839 项 · 显示 2000`，续填到 2000 生效。
- 无 DLL 回退：复制包但删除 `qbox_engine.dll`，仍以 `--search-engine rust` 启动，查询正常返回 C# 结果，进程未加载 sidecar，失败已记录。

## 阶段 4：验收汇总（2026-11-16）

正确性：

- C# `scripts/verify.ps1`：构建 0 警告/0 错误；匹配 685/685、行为 2941/2941。
- Rust `cargo test --lib`：6 项全过；`engine-harness`：685/685、36/36、2058/2058。

静态 `.qbi`（复制索引，1 预热 + 5 次，空历史）：

| 查询 | C# median (ms) | Rust median (ms) | 加速比 |
| --- | ---: | ---: | ---: |
| `1` | 132.208 | 85.047 | 1.555× |
| `豆包` | 0.289 | 0.099 | 2.919× |
| `CODE` | 181.605 | 88.664 | 2.048× |
| `python` | 47.644 | 22.783 | 2.091× |

四查询首屏与总数一致；`1` 对照更严的 135.401 ms C# 基线仍约 1.59×，达到 ≥30% 门槛。

live `.qbd` + 应用 + 快捷方式 + 真实历史（1 预热 + 5 次）：

| 查询 | C# median (ms) | Rust 引擎 median (ms) | 加速比 | FFI median (ms) | 首屏 |
| --- | ---: | ---: | ---: | ---: | --- |
| `1` | 376.492 | 265.896 | 1.416× | 404.942 | 一致 |
| `豆包` | 0.754 | 0.767 | 0.984× | 0.810 | 一致 |
| `CODE` | 190.965 | 192.240 | 0.993× | 184.793 | 一致 |
| `python` | 54.647 | 58.718 | 0.931× | 56.223 | 一致 |

live 路径下 `CODE`/`python` 只与 C# 打平，未达到 30% 目标；这是 overlay 路径解析复杂度导致的真实结果，未改变计时口径。FFI 的大结果还包含跨 ABI 序列化/解析开销。

门槛状态：正确性套件全绿；静态 ≥30% 达标；live/FFI 首屏一致；取消、续填 2000、单击/Enter 打开、Esc 隐藏清空、工作集回收均已验证；GUI 未提权、worker 单独提权并 Watching；C 卷 compact 替换已修复（见「基础索引替换」）；默认 C#，`rust` 仅由启动参数启用；未改匹配/排序规则，未合并主干、未 push、未发布。

## 仍挡住产品 go / no-go 的事项

- C 卷 live compact 的 `Access to the path is denied.` 已定位并修复，见下方「基础索引替换」。
- live `.qbd` 路径下 `CODE` / `python` 已不再与 C# 打平（见下方「live 路径解析优化」）；相对 C# 的加速已收回，但仍未稳定达到静态路径那种约 2×。若产品要求 live 也保持 ≥30%，需要看对照方差后决定是否再优化。
- 产品默认仍是 C#；Rust 仅在启动参数 `--search-engine rust` 下启用。合并回主干、发布新版本、push 均未执行，需用户明确同意。
- 不在本笔记里做最终产品 go / no-go。


## live 路径解析优化（2026-11-16）

本轮只改 Rust 引擎的 live 路径，不改匹配、排序、历史规则，也未做界面重写或新的试用窗口。对照仍使用只读副本 `work/rust-validation/live-indexes`（`C.qbd` 2,513,609 字节，不是 `trial-state` 里已自长的那份），口径与此前相同：1 预热 + 5 次，中位；含 presence / CanMatch / collector / Finish；不含进程启动、mmap/load、PrepareSearch、预热。查询 `1` / `豆包` / `CODE` / `python`。C# 用 `tools/QBox.EngineBench --live --catalogs`，历史来自 `%LOCALAPPDATA%\QBox\history.json` 的只读拷贝 `work/rust-validation/history-readonly-copy.json`（只读源文件，未写回）。Rust 用同一份目录快照与 `--history-set` 的 `index-bench` 进程内引擎路径，不经 FFI。

代码改动：

- 预计算发生过目录变更的记录号。父链上没有任何目录变更时，可达性与路径按基础索引解析（优先非 DOS 名，与 live 规则一致），不再对每个候选做带锁的 live 父链检查。
- 查询缓存改为 `SeededMap`：开始时只克隆 `Arc`，新结果写入 `fresh`；`fresh` 为空时直接查共享表，避免小查询拷贝整表，也避免大查询在空表上多一次探测。
- 打开 `.qbi` 时使用 `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`，这是读端允许旧文件被改名挪走所必需的；但仅此不够，见下方「基础索引替换」。

正确性：`cargo test --lib` 7 项全过（原 6 项 + 未变父链路径用例）；`engine-harness` 仍是 **685/685、36/36、2058/2058**。未改 C#，故未重跑 `scripts/verify.ps1`。

官方对照是同一时段连续跑完的 C# `csharp-bench-live-opt2.json` 与 Rust `rust-bench-live-final.json`。命中与首屏（id / name / target / kind / score / group / application / direct）四查询完全一致。

| 查询 | 命中 | 首屏 | C# 中位 (ms) | Rust 引擎中位 (ms) | 加速比 |
| --- | ---: | --- | ---: | ---: | ---: |
| `1` | 1,407,763 | 一致 | 392.321 | 332.437 | **1.180×** |
| `豆包` | 6 | 一致 | 1.105 | 0.722 | **1.532×** |
| `CODE` | 232,738 | 一致 | 241.096 | 158.708 | **1.519×** |
| `python` | 104,992 | 一致 | 68.605 | 50.446 | **1.360×** |

同一套索引上，优化前 Rust live 为 `1` 1.416×、`豆包` 0.984×、`CODE` 0.993×、`python` 0.931×；`CODE` / `python` 已从打平收回。本轮四查询中位都不慢于同一索引上的 C#。`1` 在这组官方数字里是 1.18×，没有稳定跨过 30% 门槛；更安静的一组（`csharp-bench-live-opt.json` / `rust-bench-live-opt4.json`）是 `1` 396.566 / 271.425（1.461×）、`豆包` 0.712 / 0.605（1.176×）、`CODE` 227.839 / 146.604（1.554×）、`python` 75.614 / 42.817（1.766×），首屏同样完全一致。live 仍明显慢于静态 `.qbi`（静态 `CODE` / `python` 约 2.0×），剩余成本来自确有目录变更的祖先链，以及「1」这类大查询仍要为匹配的 EXE 解析路径。

未改主干 `D:\Work\workspace\QBox`（仍为 `codex/feat-new` / `75b9a92`），未 push、未发布、未动生产索引；`live-indexes` 的 mtime 与大小未变。产品默认仍是 C#。本轮不做 Rust UI。

## 基础索引替换（2026-11-16）

根因不是环境或缺少 `FILE_SHARE_DELETE`。C# `MemoryMappedFile` 在 `Load` 结束时已关掉 `FileStream`，只留 section / view，因此官方 `File.Move(tmp, qbi, overwrite: true)` 能覆盖正在映射的 `.qbi`。Rust `memmap2` 会 `DuplicateHandle` 并在 `Mmap` 生命周期内一直握着文件句柄；只要这个句柄还在，同进程或跨进程的 `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` 都会返回 error 5（`Access to the path is denied.`）。Rust 1.100 的 `File::open` 默认已带 `FILE_SHARE_DELETE`，先前补上的共享模式因此不够。

修复改在 `VolumeIndexStore.Save`：先把旧 `.qbi` 改名挪走，再把新文件落到原名，读端继续使用旧映射。失败时尽量把备份改回原名；`Load` / `AcquireWriter` 若只看到 `.old` 会先恢复。官方仅 C# 映射的路径同样走这条替换，行为更稳，不改变搜索结果。新增检查：读端除映射外再握一个带 `FILE_SHARE_DELETE` 的句柄时，`Save` 仍须成功且旧查询完整。`QBox.Checks` 行为检查 2942/2942 通过。

实机：隔离包 `work/live-opt-fix-package`，状态 `work/live-opt-fix-state`（从 `live-opt-state` 复制，含已胀大的 `C.qbd`）。GUI PID 37452（未提权，已加载 `qbox_engine.dll`），worker PID 31296。C 卷在 16:37:04（Asia/Shanghai）完成 compact：`C.qbi` 由 212,915,109 / 12:14:17 / SHA-256 `DF1D6D91FF7807A3EC3FE734C12AC8D9522921CDEBA0FEBB2959F9CB8F43953B` 换成 220,499,105 / SHA-256 `71DE4E6E1BDCC76D961B1DC96A47AE453D7B8924C0D144C3D00F9E1BC139A94E`，`C.qbd` 从约 27 MB 重置后正常续写，状态为「实时更新中」，无拒绝访问。随后 90 秒 `C.qbi` 不再重写，worker 工作集约 23 MB、私有内存约 29 MB，没有每 30 秒再冲到 1 GB。D 卷仍为「实时更新中」，本轮未观察到 D 的 compact。GUI 仍运行时，CLI `--search-engine rust` 对同一索引目录查询 `python` 返回 104,999 命中。
