# QBox

Windows 文件、目录和应用的快捷搜索工具，后续扩展为剪贴板与截图工具箱。当前产品构建版 **0.2.8**；功能变化见 [CHANGELOG](CHANGELOG.md)，接续目标见 [ROADMAP](ROADMAP.md)。

## 开发者快速开始

环境：Windows x64、本地 NTFS 分区、Git、PowerShell 7、.NET SDK **10.0.110**（或同一 10.0.1xx 特性带的后续补丁，见 [global.json](global.json)）。SDK 包含构建所需框架；仅运行发布版时需要 .NET 10 桌面运行时。

```powershell
git clone https://github.com/lqiaoqing/QBox.git
cd QBox
pwsh -File scripts/verify.ps1
pwsh -File scripts/dev.ps1
```

`verify.ps1` 构建解决方案并运行含留出样本的全部检查，不需要管理员权限。`dev.ps1` 创建少量真实样例及其查询快照，用独立配置启动开发窗口，默认不启动全盘索引、不读取正式历史，唤起键为 **Ctrl+Alt+空格**。样例可搜“中文资料”“资料”“Alpha”“doubao”。

普通开发不需要 Listary、Craft Anything、Ghidra、原软件安装包、作者的索引或个人配置。样例生成只读取普通文件身份，不读取原始卷。需要真实全盘索引时，再按 [开发指南](DEVELOPING.md) 操作。

| 入口 | 用途 |
|---|---|
| [DEVELOPING.md](DEVELOPING.md) | 项目职责、调试、数据隔离、验证、发布与排障 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 修改边界、验证和 PR 要求 |
| [ROADMAP.md](ROADMAP.md) | 已完成能力、剩余目标和暂停事项 |
| [docs/README.md](docs/README.md) | 验收、历史设计和可选研究资料 |

## 当前能力与边界

- 支持文件/目录名称、应用入口、大小写和模糊有序匹配、中文高亮；提供全拼、首字母及音节前缀补充检索。
- 查询关联历史会影响排序，成功提交打开请求后学习；身份明确时改名/移动迁移历史，硬链接歧义不猜测。
- 本地固定 NTFS 卷的 MFT 初建、USN 增量与后台合并；普通窗口与独立管理员工作进程分离，失败保留可用视图。
- 0.2.1 增加 Unicode 稀疏预筛，修复汉字查询遍历全索引的延迟。详细结果见 [对照数据](docs/2026-11-14-搜索补全/unicode-prefilter-validation.json)。
- 0.2.2 共享只读基础索引、按需生成路径并流式读取应用快捷方式，降低常驻内存；验证见 [内存优化记录](docs/2026-11-15-内存优化.md)。
- 0.2.3 增加有距离上限的名称共享、低频 ASCII 稀疏预筛，并跳过无变化重查和隐藏窗口的前台任务；实际收益与性能边界见 [续作记录](docs/2026-11-15-内存优化续作.md)。
- 0.2.4 在窗口持续隐藏后回收工作集，并按 Listary 习惯分阶段展开快捷窗口与完整界面；完整列表物化上限为 2000 条。匹配、排序和历史不变。
- 0.2.5 将隐藏后工作集回收改为约 10 秒；完整界面改为一次续填到 2000 条；已显示的当前查询可用单击或 Enter 打开。匹配、排序和历史不变。
- 尚未完全达到 Listary 的冷启动相关性标准，排名规则研究暂停。剪贴板、截图模块未开发；未将非 NTFS/网络卷列为已支持能力。

产品运行不加载 Listary，不调用其搜索接口或读取其配置/历史。完整边界见 [ROADMAP](ROADMAP.md)。

## 使用发布版

运行 [`releases/0.2.9/QBox.exe`](releases/0.2.9/QBox.exe)，默认使用 C# 搜索。普通用户配置、历史、错误日志在 `%LOCALAPPDATA%\QBox`。先从托盘退出旧版，避免再次启动只唤回旧实例。Rust 搜索引擎（`--search-engine rust` / [0.2.6](releases/0.2.6/QBox.exe)）已暂时弃用，不再作为后续验证目标；如需历史对照，0.2.9 仍保留了 `--search-engine rust` 试验入口（同目录需保留 `qbox_engine.dll`）。

另有可选的 [0.2.3-global.1 全局名称共享试用版](releases/0.2.3-global.1/README.md)，使用独立程序和数据目录，用于比较内存与查询体验。

旧 v1 索引可直接读取，名称共享在新建/基础合并后生效；0.2.2 无法读取新 v2，降级前保留旧索引副本或使用旧版重建。历史与设置格式不变。

- 默认双击 Ctrl 唤起或收起；上下键选择，Enter 或单击打开，Esc/关闭按钮收起到托盘。退出程序使用托盘菜单。
- Alt+1～9 结果快捷键默认关闭，可在菜单自行启用；不兼容 Ctrl+数字。避免与截图工具热键冲突。
- 更多结果按钮或 Alt+↓ 扩展显示，Ctrl+Shift+C 复制选中路径。
- 完整索引及后台更新需要独立工作进程的 Windows 管理员授权；窗口保持普通权限。可以暂停或关闭启动时自动更新，拒绝授权不会循环请求。
- 索引和后台任务属于当前电脑，不随仓库分发。没有索引时可从菜单建立自己的索引；没有安装服务或计划任务，后台生命周期由窗口及租约管理。

## 验证与发布

```powershell
pwsh -File scripts/check-repository.ps1
pwsh -File scripts/verify.ps1
pwsh -File scripts/publish.ps1 -OutputRoot work/package
```

普通构建没有外部 NuGet 包依赖，`NuGet.Config` 清空包源。`QBox.Checks` 是控制台检查程序，不是 `dotnet test` 项目；务必使用 `verify.ps1`。当前源码验证为 685 组匹配检查、2,939 项行为检查，真实 UI/卷操作另有验收记录。

发布脚本可以从没有 `obj` 的干净仓库直接执行，自动 restore；输出含 CLI、桌面程序及项目/第三方许可。运行窗口中的 EXE 可能被占用，开发时建议输出到 `work/package`，不要覆盖正在运行的正式目录。

[Windows CI](.github/workflows/windows.yml) 从干净 checkout 发布，再运行仓库材料检查、构建/行为检查和隔离样例查询。CI 不采集真实磁盘索引、不启动 GUI，也不代替实机验收。

## 许可

QBox 采用 [MIT](LICENSE)。第三方读音表的来源、固定版本与许可见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md)。分发发布物时保留输出目录中的许可声明。
