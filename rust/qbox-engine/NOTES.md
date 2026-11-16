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

这些仍不是实盘 `.qbi` 遍历，也不是与 C# 引擎路径在同一份复制索引上的公平首屏对比。本次未做 go / no-go。

## 本轮未做的下一步

只读 `.qbi`（魔数 `QBXNTFS1`，格式在 `VolumeIndexStore.cs`）尚未移植。本机已有可复制的只读来源（本次只查看存在性，未复制、未写入）：

- `D:\Work\workspace\QBox\work\indexes\{C,D}.qbi`
- `D:\Work\workspace\QBox-first-paint\work\first-paint-state\indexes\{C,D}.qbi`

下一步应把索引复制到本工作树自己的 state 目录后再做只读 mmap 遍历，并用同一份副本跑 Rust 与 C# 引擎路径的公平首屏对比：`1` / `豆包` / `CODE` / `python`（命中集合、排序、首屏耗时）。C# 试用包上次 UI 实测仅作参考，不能单独作为公平对照：`1` 2336.7 ms / 1,406,752 hits shown 200；`豆包` 324.8 ms / 6；`CODE` 1007.1 ms / 232,713 shown 200；`python` 289.1 ms / 104,992 shown 200。

禁止写入 `D:\Work\workspace\QBox\work\indexes`、`%LOCALAPPDATA%\QBox` 或试用包状态目录。未启动正式版 0.2.4 / 0.2.5，未触碰 `QBox-first-paint` 试用进程，未 push。
