# QBox

先实现 Windows 全局搜索，再扩展为工具箱。验收标准是用户实际使用 Listary 快捷搜索栏时的覆盖、相关性、即时响应和学习效果。

## 当前状态

已建立独立的匹配与历史核心、真实盘索引、原生窗口版和查询 CLI。产品运行不加载 Listary，不调用其搜索接口，也不使用其配置或历史。

- ASCII 模糊对齐分值已与 685 组原始指令模拟结果核对，包括 128 组留出样本。
- 支持中文及补充平面字符的有序匹配、高亮和大小写处理；应用入口已增加完整音节的全拼补充匹配，保留中文高亮。完整文件/目录拼音与 Listary 等价仍未完成。
- 已实现相同查询/前缀查询的历史优先分组、每目标 8 条关联查询、全局 500 条最近查询，以及成功提交打开请求后学习。
- 历史原子保存、损坏文件保护和多进程版本冲突合并均有自动验证。
- 已接入新增恢复的普通分桶、比较器和历史同时间稳定顺序，并与 2,048 组原始排序指令核对；Listary 的完整来源键和过滤仍未恢复。内部对齐分数不等于最终排名。
- Windows 平台项目已接入只读卷、MFT 初建、跨记录属性列表、缓存视图核对、USN 更新和原子保存；C、D 两盘已完成真实初建、增量变更和重启查询验证。
- 原始名称包含 DOS 短名；搜索时作为长名别名匹配，避免重复占位。快捷方式按去掉 `.lnk` 的名称匹配，实际启动路径仍保留快捷方式。
- 索引查询只维护待展示的前若干项，最终才生成路径和高亮；总命中数仍来自完整输入。普通排序结果与完整排序逐项对照。
- 名称字符位图减少 ASCII 查询扫描，完整匹配仍负责顺序、重复字符及高亮；Unicode 查询回退完整扫描。开始菜单/桌面中可解析的相同快捷方式动作已合并，别名仍可搜索，不同参数等动作保留。

**窗口版 0.1.3 增加应用入口全拼匹配。0.1.2 已按用户要求保存为 Git 基线 `v0.1.2`；后续大幅修改规则前先确认。普通排序、完整来源覆盖、全盘拼音和常驻 USN 等待办仍保留。**

## 窗口版

直接运行 `artifacts/QBox/QBox.exe`。这是单个 Windows x64 可执行文件，使用本机已安装的 .NET 10 桌面运行时，不需要从终端启动。

- 默认双击 Ctrl 唤起或收起；`Esc` 或右上角关闭按钮收起到托盘，托盘菜单可退出。左右 Ctrl 均支持；Ctrl+C/V 等组合键和长按不触发。
- 输入后自动搜索，上下键选择，回车或双击打开，`Alt+1～9` 打开对应项。`Ctrl+1～9` 仍兼容，但可能被现有截图软件占用。
- 更多结果按钮或 `Alt+↓` 扩展显示，未用显示条数截断总命中数。`Ctrl+Shift+C` 复制选中路径。
- 索引载入后驻留内存；再次双击程序会唤起已有实例。
- 菜单中的“更新索引”会启动独立管理员工作进程；界面本身保持普通权限。更新完成自动载入新快照，取消和失败保留已保存索引。当前为手动更新。
- 可选择索引目录、重新载入，以及更换唤起快捷键；不会修改 Listary 或截图工具的设置。

当前工作区发布版默认复用 `work/indexes` 的 C/D 盘索引。移动程序后，可在菜单中重新选择索引目录，或建立索引。设置、历史及错误日志默认保存在 `%LOCALAPPDATA%/QBox`。

开发验证可显式传入 `--index-directory <目录> --state-directory <独立状态目录>`。真实界面测试记录见 `docs/desktop-validation-2026-11-13.md`。

## 构建与验证

需要本机已安装的 .NET SDK 10.0.110 或相同特性带的后续补丁。第一阶段不使用外部 NuGet 包。

```powershell
.\scripts\verify.ps1
```

当前验证结果：685 组原始指令分值对照、2,784 项行为/磁盘格式边界检查通过，后者包含 2,048 组排序指令对照，Release 构建无警告。新增全拼及与基线对照见 `docs/application-pinyin-validation-2026-11-14.md`。

## 运行

```powershell
dotnet run --project src\QBox.Cli -c Release -- match nd "Neat Download Manager"
dotnet run --project src\QBox.Cli -c Release -- search --catalog samples\catalog.example.json --query ed --history work\example-history.json
```

示例候选是说明格式的虚构路径，不代表已建立索引。换成自己的候选 JSON 后即可查询。默认历史保存在 `%LOCALAPPDATA%\QBox\history.json`；`--history` 可以指定独立验证文件。

`open --catalog <文件> --query <查询> --id <ID>` 会请求 Windows 打开对应目标，成功提交后才记录历史；`search` 不修改历史。Shell 已接受请求不等于目标应用已完成加载。

候选 ID 必须稳定且表示同一启动动作，不能只用显示名称。一个 ID 可以具有多个名称别名；查询后才去重。显示条数不影响总命中数。

已发布的命令行程序在 `artifacts/QBox.Cli/QBox.Cli.exe`，需要本机 .NET 10 运行时。运行 `scripts/publish.ps1` 更新发布文件。

管理员 PowerShell 中建立、同步一个 NTFS 卷的索引：

```powershell
.\artifacts\QBox.Cli\QBox.Cli.exe volume-probe --drive C:
.\artifacts\QBox.Cli\QBox.Cli.exe index-build --drive C: --index work\indexes\C.qbi
.\artifacts\QBox.Cli\QBox.Cli.exe index-sync --drive C: --index work\indexes\C.qbi
```

普通权限查询已保存的快照：

```powershell
.\artifacts\QBox.Cli\QBox.Cli.exe search --index work\indexes\C.qbi --query .code
.\artifacts\QBox.Cli\QBox.Cli.exe search --index-directory work\indexes --query 800Q
```

`scripts/index-local.ps1` 在管理员进程中依次为本机固定 NTFS 盘符初建或同步，索引保存在 `work/indexes`，日志保存在 `work/native-index-*`。可用 `-DriveLetters CD` 限定盘符。原始卷只读，只有索引/报告写入工作区。失败、取消、日志断档保留上次文件；索引附 SHA-256 校验，损坏文件不自动覆盖。查询不自动追赶日志，离线快照可能过期。硬链接路径分别保留，重命名后的路径身份尚未迁移历史。打开索引目标前核对实际卷和文件身份，阻止过期路径启动被复用的新文件。

本轮管理员会话和索引进程均已退出，没有安装服务或计划任务。`scripts/index-session.ps1` 是开发时用于本任务 C/D 两盘重试的 15 分钟临时入口，不属于常驻索引服务。

## 接续工作

1. 恢复普通排序元数据，处理无历史时 ANYconsole 和 `.codex` 排名不足的问题；完善中文拼音和对齐验证。
2. 扩展应用来源与常驻 USN 更新；开始菜单/桌面的可解析动作合并、打包应用注册入口和便携 EXE 已接入。
3. 完成窗口版的实际使用验收与桌面兼容性检查。

实施清单见 `docs/plans/2026-11-13-search-implementation.md`。研究资料在 `work/research`，不属于产品运行依赖。

Listary 优先级默认表及九类查询的完整集合离线对照见 `docs/priority-study-2026-11-14.md`。简单降级会损害 `.codex` 排名，因此本轮仅保留分析工具与证据，未将实验规则接入 0.1.3。

后续已恢复规则合并及 64 位引擎排序键的计算，并通过 500 + 780 组原始指令对照，见 `docs/priority-computation-2026-11-14.md`。设置到引擎的两次类别转换及 HKCR 注册后缀来源也已核实，新增 256 个设置字节、800 个类别字段指令对照，见 `docs/priority-codec-and-suffixes-2026-11-14.md`。来源封装及完整行为仍待闭合，当前产品规则保持不变。
