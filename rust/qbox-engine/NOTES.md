# QBox Rust 搜索引擎校验（Day 1）

日期：2026-11-16（Asia/Shanghai）
工作树：`D:\Work\workspace\QBox-rust-engine`
分支：`feat/rust-engine-validation`
基线提交：`75b9a9275daf2d3d900d64c3308fb06d015f67c3`（`release: publish QBox 0.2.5`，父提交 `c515133`）
原始仓库 `D:\Work\workspace\QBox` 仍在 `codex/feat-new`，本任务只通过 `git worktree add` 建树，未改主干工作区，未 push。

## 工具链

本机原先没有 `rustc` / `cargo` / `rustup`，也没有 Visual Studio / MSVC `link.exe`。已用 rustup-init 为当前用户安装：

- rustup 1.29.3（edbc831ce 2026-11-02）
- rustc 1.100.0（0ef1bc386 2026-11-09）
- cargo 1.100.0（3acc79c0c 2026-11-05）
- 默认工具链：`stable-x86_64-pc-windows-gnu`
- 安装位置：`C:\Users\zsj\.cargo`、`C:\Users\zsj\.rustup`
- 安装器缓存：`work/rust-setup/rustup-init.exe`（已 gitignore）

未装 MSVC 的原因：本机没有 vswhere / Visual Studio Build Tools。系统里虽有 MinGW-w64 gcc 8.1.0 和旧的 `C:\MinGW` gcc 4.8.1，但不能直接拿来链接 Rust 1.100 的 GNU 目标。

本机 Windows 11 家庭版 10.0.26200 的 `kernel32.dll` 不再导出 `WaitOnAddress` / `WakeByAddressAll` / `WakeByAddressSingle`（这些符号在 `api-ms-win-core-synch-l1-2-0.dll` 和 `kernelbase.dll`）。若用系统 gcc 8.1 链接，生成的 exe 会从 `KERNEL32.dll` 导入上述符号并在启动时失败（`0xC0000139`）。cargo/rustc 官方二进制本身已从 API set 导入，所以编译器能跑。校验 crate 必须使用 rust-mingw 自带链接器与导入库：

- 链接器：`$(rustc --print sysroot)\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained\x86_64-w64-mingw32-gcc.exe`
- 通过 `rust/qbox-engine/gnu-linker.cmd` 与 `.cargo/config.toml` 中的 `link-self-contained=yes` 固定
- 构建产物目录：`work/rust-target`

## Crate 与对照

- Crate：`D:\Work\workspace\QBox-rust-engine\rust\qbox-engine`
- 匹配器：`src/fuzzy.rs`，按 `src/QBox.Core/Matching/FuzzyMatcher.cs` 逐段移植（CanMatch、ASCII 单字符 IndexOf 快路径、ScoreMatched、完整 Match / 高亮、词段边界、缩写路径）
- 样本：直接读取 `tests/QBox.Checks/Fixtures/ascii-baseline.json`（12）和 `ascii-corpus.json`（673），不改写、不丢弃 holdout
- 运行：`cargo run --bin match-harness`（默认包含 holdout，与 `scripts/verify.ps1` / `--include-holdout` 相同）

## 685 组结果

**685/685 通过**（含 128 组 holdout；留出未执行 0）。

`match_text` 的 `alignment_score` / `is_match` 与 `score` 快路径均与 oracle 分值一致，无失败样本。明细文件：`work/rust-validation/failures.txt`（`failed=0`）。

这 685 组全部是 ASCII 内部对齐分值，不是 Launcher 最终排序、不是拼音、也不是实盘首屏。

## Day 2 建议下一步

不要在这一步做 go / no-go。685 只证明 FuzzyMatcher 对齐分值可以按测试逐条移植。下一步应按这个顺序：

1. 移植 `PreparedApplicationName` 拼音读音与 `tests/QBox.Checks/PinyinChecks.cs`，对照同一份 `pinyin.tsv`。
2. 移植 `SearchCollector` 与 `LauncherOrdering`（不要并行化会改变收集序号的扫描），用现有 C# 行为检查作对照。
3. 只读遍历 `.qbi`（`QBXNTFS1` / mmap）。若需要实盘索引，只能复制到本工作树自己的 state 目录，禁止写入 `D:\Work\workspace\QBox\work\indexes`、`%LOCALAPPDATA%\QBox` 或试用包状态目录。
4. 在复制出的索引上做公平首屏对比：`1` / `豆包` / `CODE` / `python` 的命中集合与排序，以及 `1` 的首屏耗时。C# 试用包上次实测（仅作对照基线，Day 1 未重测）：`1` 2336.7 ms / 1,406,752 hits shown 200；`豆包` 324.8 ms / 6；`CODE` 1007.1 ms / 232,713 shown 200；`python` 289.1 ms / 104,992 shown 200。

未启动正式版 0.2.4 / 0.2.5，未触碰 `QBox-first-paint` 试用进程，未 push。
