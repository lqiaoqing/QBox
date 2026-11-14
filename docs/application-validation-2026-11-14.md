# QBox 0.1.2 应用检索修复

## 两个用户报告的原因

`chatgpt`：旧版本匹配出 645 个文件/目录，ChatGPT.exe 已在索引中，但只能以普通文件出现在第 32 项。系统 Shell 的 AppsFolder 中实际存在名称为 ChatGPT 的包注册入口，旧版没有接入该来源。包身份为 `OpenAI.Codex_2p2nqsd0c76g0!App`，应通过注册入口激活。

`rtt`：真实索引中存在 `C:\Users\zsj\.eide\tools\jlink\JLinkRTTViewer.exe`，文件也确实存在。旧版没有将没有开始菜单快捷方式的 EXE 识别为应用，又将分值为 1 的弱匹配快捷方式放在应用优先桶，导致目标被挤在后面。这属于功能缺陷，不能作为正常验收结果接受。

读取 Get-AppxPackage 曾被当前环境拒绝；改用系统 Shell 只读枚举，无需提升 GUI 权限。没有修改 Listary 或导入它的历史，也没有收集 Listary 性能数据。

## 实现

- 从 Shell AppsFolder 只读读取打包应用名称与 AUMID，使用 AUMID 的 SHA-256 作为稳定身份，不绑定会随升级变化的安装目录。启动提交使用解析后的 Shell 虚拟对象，包目录 EXE 继续作为普通文件保留。图标也通过虚拟对象读取。
- 直接 EXE 在名称获得强匹配时成为可启动应用候选；明确查询 `.exe` 仍保留原名及普通文件入口。打开直接 EXE 时使用其所在目录作为工作目录。
- 已注册/快捷方式定义的应用与直接 EXE 分为两层，后者排在前者之后、普通目录之前。分值为 1 的弱应用仍可查询，但不无条件提权。匹配快捷方式已代表某个 EXE 时，该 EXE 保留普通文件入口，避免重复占应用位置。
- 每一无查询历史的应用层跨来源按匹配分值择优，历史关联优先。首屏已满后仍允许晚到的更好候选进入，并完整保留命中计数。
- 修正无历史应用误显示“常用”的提示。应用注册列表读取失败时保留文件搜索并明确报告；仅有注册应用而尚未建立文件索引时，窗口也可以搜索这些应用。

新增来源层及无历史应用的跨来源比较是 QBox 的修复策略，不是已经证明等价的 Listary 完整来源排序。原始比较器的 2,048 组指令对照仍保留，但不能将其外推为这些新来源策略已经完整逆向。

系统接口依据为 Microsoft 的 [AUMID 与 AppsFolder 查询](https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid)、[Shell 对文件及虚拟对象的启动](https://learn.microsoft.com/en-us/windows/win32/shell/launch)、[SHELLEXECUTEINFO](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow) 和 [SHGetFileInfo 的 PIDL 标志](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetfileinfoa)。

## 验证

`scripts/verify.ps1`：685 组匹配分值对照、2,756 项行为/格式检查通过，Release 无警告、无错误。新增检查覆盖 AUMID 身份与名称别名、无效/不一致启动目标、失败不学习、弱应用降级、便携 EXE、包目录 EXE、已有快捷方式的重复提权、有界排序与完整排序，以及来源交错和历史反转。

真实 C/D 快照及当前系统注册列表识别出 46 个包应用入口，没有读取警告。空历史的搜索回归如下。

| 查询 | 结果 |
|---|---|
| chatgpt | ChatGPT 第 1，系统入口可解析 |
| rtt | RTT Client 第 1、RTT Logger 第 2、RTT Viewer 第 3；Viewer 对应真实存在的 EXE |
| 800Q | EG800Q 第 1 |
| nd | Neat Download Manager 第 1 |
| Tra | Trae CN 第 1 |
| Any / .code | 保留原目标召回；无历史排序仍需继续改进 |

还确认不存在的模拟包不能解析为有效入口。回归程序未实际启动用户的 ChatGPT、RTT Viewer 或连接调试硬件；这里证明的是搜索结果、入口解析与启动代码的检查范围，不能称为完整真实点击验收。

过程发现的两种退化都已处理：仅提升 EXE 而不统一比较，会让先遍历的系统 EXE 占满首屏；将所有 EXE 与已定义应用同层竞争，又会使 NetDiag 等名称压过 Neat Download Manager。最终上述真实回归及自动检查通过。日志为 `work/verify-applications-20261114.log`、`work/application-diagnosis/after-fix.jsonl`、`work/application-diagnosis/published-*.json`。

## 发布与后续

已发布并启动 `artifacts/QBox/QBox.exe`，版本 0.1.2.0，445,709 字节，SHA-256 `260c2bcab2d7fcc482b9481a73217e1fdbe4653479523b6151ee02bf594b834f`。旧版备份于 `work/release-20261114-apps/QBox-before-update-20261114-080850.exe`；设置、历史及真实索引没有替换。

完整 Listary 来源排序仍未恢复，主要待追踪的是候选来源键生成、过滤条件和元数据标志。继续从元数据构造器向上追踪字段的生成，再做原始指令对照和案例回归。系统应用列表当前随启动/重新载入刷新，文件仍依赖现有快照；常驻更新、完整中文拼音、其他未覆盖入口和重命名历史迁移继续按原计划处理。当前不需要用户再提供安装包、截图或性能数据。
