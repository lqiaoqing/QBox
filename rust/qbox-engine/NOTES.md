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

## 仍挡住产品 go / no-go 的事项

- 本轮证明静态 `.qbi` 首屏可以对齐，并且去掉热路径字符串拷贝后，Rust 引擎路径在 `1` / `CODE` / `python` 上中位快于 C# 引擎路径至少 30%。尚未接入 UI / FFI，也未覆盖应用、快捷方式和 live `.qbd` 覆盖层。
- 不在本笔记里做最终产品 go / no-go。
