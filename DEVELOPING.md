# 开发指南

本指南面向新机器与新开发者。先跑通 README 的快速开始，再按问题涉及的模块阅读代码。当前状态和下一步看 [ROADMAP](ROADMAP.md)，不从历史实施清单重新做一遍已完成工作。

## 环境

- 已验证开发路径为 Windows x64、本地 NTFS 工作目录、PowerShell 7、Git 和 .NET SDK 10.0.110。
- `global.json` 使用 `latestPatch`，限制在相同 SDK 特性带。先运行 `dotnet --list-sdks` 与 `dotnet --version`；若不满足，按 [微软 Windows 安装说明](https://learn.microsoft.com/dotnet/core/install/windows) 安装相应 SDK，而不是只装运行时。
- PowerShell 中通过 `pwsh` 执行脚本。NuGet 包源当前为空；框架引用来自 SDK，不需要 Ghidra、Python、Listary、原软件二进制或额外测试框架。
- 本指南的命令从仓库根执行。路径可以包含空格。不要把作者机器上的目录或历史当成依赖。

## 工程地图

| 项目/目录 | 责任与优先阅读入口 |
|---|---|
| `src/QBox.Core` | 字面/拼音匹配、排序、候选与历史；`FuzzyMatcher`、`SearchCollector`、`SelectionHistory` |
| `src/QBox.Windows` | NTFS、索引视图、应用/快捷方式、身份校验；`NtfsIndexBuilder`、`LiveVolumeIndex`、`NamePresenceIndex` |
| `src/QBox.Desktop` | WPF、热键、查询取消、图标和后台生命周期；`App`、`MainWindow`、`LiveIndexWorker` |
| `src/QBox.Cli` | 查询、匹配、索引及打开的命令行入口；`CliApplication` |
| `tests/QBox.Checks` | 无外部测试包的自动检查与夹具；`Program.cs` 是执行入口 |
| `tools/QBox.DevData` | 生成当前机器上的小规模开发快照，不扫描全盘 |
| `tools/QBox.MemoryProbe` | 内存、完整结果摘要及确定性索引速度重放；见工具 README |
| `tools/QBox.DesktopProbe` | 独立状态、屏幕外窗口的前台生命周期检查；见工具 README |
| `tools/QBox.PriorityStudy`、`scripts/research` | 可选离线研究，独立于产品和普通验证；见工具自己的 README |

主要调用链：窗口输入 → 最新查询门控 → 应用/快捷方式/索引来源 → 候选预筛 → 原匹配器与收集排序 → 当前版本结果显示。打开前核对身份，只有提交启动成功后才记录历史。后台工作进程读 NTFS 变更并发布日志，GUI 读取不可变视图；不要把权限逻辑放进普通搜索循环。

## 隔离开发窗口

```powershell
pwsh -File scripts/dev.ps1
# 只构建、准备数据，不启动 GUI（CI 使用）
pwsh -File scripts/dev.ps1 -Configuration Release -PrepareOnly
```

每次启动生成/复用 `work/dev/files/QBox Demo` 的少量样例，生成 `work/dev/indexes` 的子集快照，将开发配置写入 `work/dev/state/settings.json`。已有样例内容和开发历史不会清空；开发配置会重设为关闭自动更新、关闭数字结果热键并使用 Ctrl+Alt+空格。正式 `%LOCALAPPDATA%\QBox` 不读写。

快照只含样例及其路径所需父目录，具有当前机器的真实文件身份，因此可以在本机查询并打开样例；它不含有效 USN 恢复点，不代表整个卷。窗口仍会读取本机已注册应用，所以不同机器的应用结果可能不同。

开发期间不要点击“更新索引”或开启自动更新，除非确实准备做真实 NTFS 测试；这会进入管理员全盘初建路径。开发样例没有模仿整个磁盘，也没有运行原软件。

### 调试器

在支持 .NET 10 的 IDE 中打开 `QBox.slnx`。先运行 `dev.ps1 -PrepareOnly`，将 `QBox.Desktop` 设为启动项目，传入：

```text
--state-directory "<repo>/work/dev/state" --index-directory "<repo>/work/dev/indexes"
```

把 `<repo>` 换为本地绝对路径。也可以附加到脚本启动的开发 QBox 进程。默认配置为 Debug，`-Configuration Release` 可检查优化后的表现。不要直接以默认参数调试桌面项目，否则会使用正式状态目录并可能请求全盘索引授权。

## CLI 与自动检查

```powershell
pwsh -File scripts/verify.ps1
dotnet run --project src/QBox.Cli -c Release --no-build -- match nd "Neat Download Manager"
dotnet run --project src/QBox.Cli -c Release --no-build -- search --catalog samples/catalog.example.json --query ed --history work/example-history.json
# 运行 dev.ps1 后查询小索引
dotnet run --project src/QBox.Cli -c Release --no-build -- search --index-directory work/dev/indexes --query 中文资料 --history work/dev/state/cli-history.json
```

示例 catalog 中的路径是虚构的，适用于搜索格式演示；不要对其调用 `open`。开发快照中的样例路径是本机真实文件，可用于打开验证。`search` 不学习或写入选择历史，`open` 会启动目标并在成功后学习，测试时使用独立历史路径。

`verify.ps1` 包含留出样本，非管理员即可运行。构建成功、自动检查、内核计时、实际输入法/GUI 和真实卷恢复是不同证据，分别记录；`dotnet test` 不会代替本仓库的检查入口。

## Rust 搜索引擎试验（已暂时弃用）

> **状态说明**：Rust 搜索引擎目前已暂时弃用，后续不再进行 Rust 侧功能对齐或发布验证；日常开发与发行均以 C# 引擎为准。以下仅保留历史构建与运行说明。

默认搜索仍使用 C#。`--search-engine rust` 可用于桌面程序和 CLI 的索引查询，加载或调用失败会记录原因并回退 C#；本轮尚未提供 macOS 版本。

普通 .NET 构建和自动检查不要求 Rust。发布脚本现在还会构建并复制 `qbox_engine.dll`，因此发布环境需要 Windows x64 Rust 工具链；GNU 工具链使用仓库自带的相对路径链接器包装，MSVC 工具链需要相应链接工具。

在 `rust/qbox-engine` 目录运行 `cargo test --locked --lib` 和 `cargo run --locked --release --bin engine-harness`，可验证 Rust 匹配、拼音和排序对照。运行 `cargo build --locked --release --lib` 后，DLL 位于仓库的 `work/rust-target/release`。

桌面发布包把 DLL 放在 EXE 同级。CLI 发布包和开发输出可通过环境变量 `QBOX_ENGINE_DLL` 指向该 DLL 的绝对路径；试用时仍须使用独立状态、历史和索引副本，不启动正式全盘更新。

迁移范围、实验结果与限制见 [Rust 引擎记录](rust/qbox-engine/NOTES.md)。

## 真实 NTFS 测试

只有需要验证卷读取、USN 或真实全盘来源时才使用。先发布 CLI：

```powershell
pwsh -File scripts/publish.ps1 -OutputRoot work/package
# 以下两行在管理员 PowerShell 中运行
./work/package/QBox.Cli/QBox.Cli.exe index-build --drive C: --index work/real-indexes/C.qbi
./work/package/QBox.Cli/QBox.Cli.exe index-sync --drive C: --index work/real-indexes/C.qbi
```

普通权限可查询保存的快照：

```powershell
./work/package/QBox.Cli/QBox.Cli.exe search --index-directory work/real-indexes --query 资料 --history work/real-history.json
```

用新建隔离文件测试新增、移动、重命名、删除，不改用户已有业务文件。运行 GUI 时使用另一份独立状态目录和该索引目录；复制开发配置后修改索引路径即可，保持 `autoUpdate: false`，需要测试后台时再显式启用。卷只读不等于数据可公开，生成索引和日志不提交。

## 发布与 CI

```powershell
# CLI 与桌面完整发布，可以从没有 bin/obj 的干净仓库开始
pwsh -File scripts/publish.ps1 -OutputRoot work/package
# 只发布桌面版
pwsh -File scripts/publish-desktop.ps1 -OutputDirectory work/desktop-package
```

发布为使用 .NET 10 桌面运行时的 Windows x64 程序。输出中的 `LICENSE`、`THIRD-PARTY-NOTICES.md` 和 `licenses/pinyin-data-LICENSE.txt` 需要随发布物保留。脚本不管理 Git 标签、不上传发布物，也不会停止已运行的程序。替换正在使用的正式 EXE 前应退出对应实例；已有版本归档不要无故覆盖。

Windows CI 的检查顺序是：仓库材料检查 → 无构建缓存的发布 → 全部自动检查 → 准备开发实例（不显示窗口）→ 查询样例并核对隔离配置。工作流使用固定提交的官方 [checkout](https://github.com/actions/checkout) 与 [setup-dotnet](https://github.com/actions/setup-dotnet)，只申请 contents 读取权限。尚未推送的工作流不能宣称已在 GitHub 通过；本地干净副本验证另见 [协作整理记录](docs/2026-11-14-仓库协作整理.md)。

## 常见问题

- **SDK 无法解析**：检查 `dotnet --list-sdks` 与 `global.json`，不要为了在本机运行而直接修改仓库 SDK 约束。
- **脚本调用找不到 pwsh**：安装 PowerShell 7。普通验证不需要管理员；原始卷读取才需要。
- **找不到 artifacts**：发布物不随 clone 自动生成，先运行发布脚本，或直接使用仓库内已归档的产品 EXE。
- **开发窗口已有实例**：相同状态目录只保留一个 GUI；再次启动会唤回旧窗口。改代码后需退出该开发实例再启动，不能将旧进程表现当作新代码。
- **中文结果慢或不符**：先核对二进制版本，再检查来源、预筛、原匹配器及 UI 阶段；不要先通过改变排序、隐藏目录或截断结果掩盖问题。
- **历史计划与现在不一致**：历史文档只作证据。当前约束看 `AGENTS.md`，流程看本指南，目标看 `ROADMAP.md`。

## 材料边界

[仓库材料检查](scripts/check-repository.ps1) 检查当前文本/压缩证据和提交文件名，不扫描完整 Git 历史。历史真实机器报告中的用户目录、工作根目录和 SID 已使用占位符脱敏，数值测量仍描述当时那台机器；公开副本不能替代操作者自己的原始索引。旧提交未被重写，若需要清理历史，应作为独立任务确认范围。
