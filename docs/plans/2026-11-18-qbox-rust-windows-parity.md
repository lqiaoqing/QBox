# QBox Windows 纯 Rust 正式替换 Implementation Plan

> **实施入口**　按本文在 `QBox-rust-engine` 接着做。本文规定正式替换的剩余任务、顺序、交付与验收。`WINDOWS-TRIAL.md` 记录 A–D 的历史试用范围。下文链接的格式、权限和行为契约继续适用。
>
> **验证以 §3 为准**　旧文档中的全量矩阵、5/10/20 对统计复测、8 小时观察和验证器开发属于专项排查材料。正常实施按本文的两次短对照和一次候选包走查收口。

**目标**　交付可以替代 Windows x64 C# 0.2.5 的纯 Rust QBox。保留搜索结果与已认可的排序、桌面操作、实时索引、CLI 和旧数据升级能力。正常产品使用路径不依赖 C#、.NET 或 FFI，搜索速度不得降级。

**架构**　复用现有 `qbox-engine`、`qbox-app`、`qbox-platform`、`qbox-desktop` 和 `qbox-cli`。桌面和 CLI 共用应用层查询、启动和状态逻辑。普通权限读端保存不可变查询快照，管理员 Rust worker 负责卷读取和每卷单写者提交。

**技术栈**　Rust workspace、现有 eframe/wgpu、Windows Shell/NTFS USN、QBI v3/QBD v2 和双 manifest。沿用现有 Windows 构建环境与 `Cargo.lock`，以最终 Release 包实测。

## 1. 基线与执行边界

- 冻结对照为 `codex/feat-new@1f06daa0c9a9f29d8acbce357d169485afda92a4` 的 Windows C# 产品行为，默认走 C# 搜索。标准分发版为 0.2.5。已有冻结程序、索引、应用目录、历史、探针和结果在 Rust 工作树 `work/rust-baseline/`，以其 manifest 及哈希为准。若某项能力仅存在于较新的源码基线，兼容表标明来源，测试对象统一使用冻结源码对应程序。
- Rust 起点 `QBox-rust-engine` 的 `feat/t09-t10-worker-ntfs@ff26528`，试用 EXE 对应 `be5845d`。开工核对 HEAD、工作区和该工作树的 `docs/2026-11-17-纯Rust重写/PROGRESS.md`。若继续推进过，按本计划的完成条件认领已有成果。本文在主工作树 `docs/plans/`，任务路径均相对 Rust 实施工作树。
- 已完成并复用 T00–T10 的基线、核心结果对照、格式、世代恢复、只读导入、权限和真实卷证据，以及 A–D 的真实索引搜索、打开、双 Ctrl、托盘、单实例和独立手动同步。对受影响链路定向重验。早期核心验证与试用包都不能替代正式桌面接线的实测。
- 正式范围含 Windows 本地固定 NTFS 多卷、文件/目录/普通快捷方式/AppX 搜索与打开、历史、两种窗口、设置和热键、后台自动更新、公开 CLI、升级与发布。界面对齐以信息、操作和响应为准，保留快捷框与主界面。macOS、新工具、试验性全局名称共享版及非 NTFS/网络卷另立需求。
- 基线文件与正式用户状态只读。开发、A/B、升级、故障注入、CLI 写入均使用独立状态、独立索引根和自建样例。以 `PROGRESS.md` 一处记录每项提交、原始证据与待确认。最终切默认入口在候选分支完成，正式发布、替换用户运行目录及合并另行安排。

| 顺序 | 任务 | 依赖 | 可交付结果 |
|---|---|---|---|
| P0 | 基线清点 | 当前工作树 | 同一份可比较的输入和命令清单 |
| P1 | 历史与快捷方式对齐 | P0 | 桌面真实查询能学习，常用结果顺序和去重正确 |
| P2 | 单卷实时增量与原生身份 | P1 | 自动变更可查，可做第一次同功能速度和资源对照 |
| P3 | 多卷、恢复与后台生命周期 | P2 | 正式后台更新和故障恢复 |
| P4 | 应用目录与旧状态升级 | P1、P3 的身份接口 | 普通快捷方式和 AppX 对齐，旧用户能升级 |
| P5 | 桌面功能和资源收尾 | P3、P4 | 可用完整界面、设置、热键与结果操作 |
| P6 | CLI 兼容 | P3、P4 的应用层入口 | 公开命令、输出和副作用对齐 |
| P7 | 候选包、实机验收、切换默认入口 | P1–P6 | 对齐证据、纯 Rust 发布包、旧版回退说明 |

## 2. 实施任务

### P0　确认输入与复用范围

1. 定位冻结 C# 程序和样本，记录两个程序的提交及 EXE 哈希。先确认一个已有查询能跑通。CLI 参数、输出和退出码在 P6 按命令核对，桌面操作在 P5 按实际入口核对。
2. 优先从已保存的 C/D 基础、增量和状态副本产生同一逻辑内容的 Rust 世代。比较卷身份、日志检查点、有效文件数、应用来源、历史和查询上限。真实更新另用隔离索引跟踪同一批自建变更。排序差异先区分来源、ID、历史、匹配、排序或显示。
3. 在 `PROGRESS.md` 登记基线位置和现成验证入口。缺少输入时只补当前任务需要的一份。P0 核对完即进入 P1，样本清单、验证器增强和全量基线校准不作为开工任务。

**出口**　能从两个独立状态执行同一条查询并核对结果身份，明确哪些证据是基线、哪些覆盖产品入口。功能清单以 Rust 实施工作树 `docs/2026-11-17-纯Rust重写/FEATURE-MATRIX.md` 及 C# 公开入口为准。

### P1　打通桌面查询历史和快捷方式身份

**涉及文件**　`rust/qbox-app/src/{search_service,history_store,shortcuts}.rs`，`rust/qbox-engine/src/{catalog,live,qbi}.rs`，`rust/qbox-desktop/src/main.rs`。对照 `src/QBox.Desktop/MainWindow.xaml.cs`、`src/QBox.Windows/Shortcuts/ShortcutCatalog.cs` 和 `src/QBox.Core/History/SelectionHistory.cs`。

1. GUI 启动时读历史快照，成功保存后更新内存快照。请求发起时捕获 query、请求版本、索引版本和历史快照。后台用 `HistoryFile::to_query_history` 查出该查询的历史信号。把同一份信号给 `ShortcutCatalog::search_into_with_matches`，并用 `IndexHistoryMap::from_serials` 给文件卷查询传入历史。保留当前一执行、一最新待执行和取消约束。历史文件异常反馈，搜索保持可用。查询时不逐行读磁盘。
2. 以 C# 的快捷方式替换 ID、别名组、represented executable 规则为准。索引覆盖 `.lnk` 时，按卷及原生路径关联真实卷候选身份，当前查询中同一动作只留正确代表项。没有卷记录时继续支持独立开始菜单项，保留其稳定标识。组内动作身份包含目标、参数、工作目录及影响启动的字段。不同动作不能只因目标 EXE 相同而合并。已有试用 `lnk:` 历史可做唯一映射，有歧义保留原记录。
3. 检查激活链路从当前可见请求的候选身份出发。过期结果拒绝激活。确认 Shell 接受启动后再记录历史。保存失败独立报告，打开成功仍如实显示。

**验收**　同快照与历史下回归 `Any`、`800Q`、`.code`、`nd`、`Tra`、`chatgpt`、`rtt`，加 `1`、`python`、`豆包` 及可命中的拼音。P1 使用双方共同的文件和快捷方式来源，P4 检查 AppX 加回后受影响的查询。现有探针能直接比完整列表时复用探针。桌面快速核对同查询的结果数、前十顺序、目标身份、匹配高亮和历史提示，200/2000 的首批及续填在最终候选覆盖。另查选中后同词/前缀排序变化、重启仍有效、打开失败不学习、同一 `.lnk` 卷项不重复及不同参数动作独立。保持现有匹配与权重。

### P2　接通单卷实时增量，保持原生目标准确

**涉及文件**　`rust/qbox-engine/src/{ntfs_builder,qbi_v3,qbi,qbd_v2,generation_store,live}.rs`，`rust/qbox-app/src/{index_build,build_client,worker,search_service,native_target}.rs`，`rust/qbox-desktop/src/main.rs`。沿用 [STORAGE.md](../2026-11-17-纯Rust重写/STORAGE.md) §2–6 和 [DESIGN.md](../2026-11-17-纯Rust重写/DESIGN.md) §6。

1. 从 `NtfsIndexBuilder::synchronize` 拆出基于已有索引视图的变更批次接口。只解析和保存受影响的完整文件记录及删除项，重用对扩展记录、父链、FRN 复用和硬链接的处理。初建沿用 `build`。`BuildOnce` 从已提交检查点追赶并结束，`BuildOrWatch` 继续监听。普通变更不复制整表或写新 QBI。worker 不构建供 GUI 查询的预筛、图标或路径缓存。采用基线约 1 秒追赶，合并仅推进但无名称变化的检查点，按 STORAGE 的持久化时限刷新。
2. 接入 QBD v2 append/read-prefix。每批验证 base hash、serial、世代 UUID、journal、连续序号与检查点，持单写者锁追加并 flush，再发送持久结果。manifest 的发布前缀是已承诺的最低边界，后续已验证的完整帧可以推进同一世代的增量版本。读端捕获文件长度，只读到这次边界。未提交的半尾帧留到下轮，已承诺部分损坏按恢复协议处理。仅写者在独占状态下修复半尾后从最后持久点续跑。
3. `SearchService::reload` 和增量刷新共用同一种 `base + overlay` 查询视图。复用 `live.rs` 的覆盖、删除、路径/可达性算法，给 QBD v2 接一条原始 UTF-16 输入，保留旧 QBD 的只读适配。基础、增量、目录和历史都随请求固定。GUI 在后台生成新快照，前台仅交换可用视图并按当前输入合并一次刷新。同一世代按 delta revision 识别新结果和路径缓存，不能只看 commit_seq。无变化跳过刷新，隐藏期间只追赶状态。
4. 以 `(卷 serial, 完整 FRN, 父 FRN, 原始 UTF-16 名称身份)` 识别实际目标。基础和 overlay 都按原始单元计算 ID、走父链并调用宽字符 Shell API。检查 `VolumeIndex::from_qbi_v3` 是否经 `String::from_utf16_lossy` 重写名称，再核对 `native_target.rs` 实际读到的是哪一份名称池。确保孤立代理项经过匹配、身份、显示和激活后，展示用替代字符不会回流到原生目标解析。激活前核对当前请求与快照有效性及目标身份，避免同路径新对象冒充旧结果。ASCII、数字及拼音复用既有热路径与预筛。
5. 首次和换世代只验证一次不可变基础内容，优先共享底层映射/名称池，复用已准备的数据。同世代只读新增日志、更新受影响缓存。若仍需物化成本较大的视图，用阶段采样分开记录校验、加载、准备、搜索和交换峰值，再决定是否改变布局。

**验收**　复用 `ntfs-replay`、`ntfs-real`、`ntfs-worker`。用一个真实 NTFS 卷证明新建、改名、跨目录移动、目录子树移动、删除和多硬链接可自动反映。差异位置的 FRN/原始名称正确，正常批次不写新 base。覆盖写入中断、半尾、重启补追、无变化跳过、旧快照在途查询、孤立代理项与普通替代字符同名。收集一次相同单卷、两边开启实时更新的短对照。若出现可重复降速或索引反复全量准备，定位后再扩大到真实多卷。

### P3　多卷、在线合并、故障恢复与权限

**涉及文件**　`rust/qbox-app/src/{worker,worker_protocol,index_build,build_client,search_service,trial_settings}.rs`，`rust/qbox-engine/src/{generation_store,manifest_store}.rs`，`rust/qbox-platform/src/windows/{volumes,ntfs,secure_fs,pipe,token}.rs`，`rust/qbox-desktop/src/main.rs`。对照 `src/QBox.Desktop/{LiveIndexWorker,IndexUpdateJob}.cs` 及 [WORKER-PROTOCOL.md](../2026-11-17-纯Rust重写/WORKER-PROTOCOL.md)。

1. 将试用单 `volume` 设置及单一根迁到 `indexes/<serial-16hex>/` 的每卷存储。显式绑定选中卷真实身份和根，盘符用于呈现及定位。有效卷组成一个发布给搜索的视图，更新一卷复用其他卷的快照。重复入口拒绝双计数，卷移除、盘符变化、签名变化、离线、未建、权限失败分别反馈。初建或故障重建最多一个重任务，异常卷不阻塞其他卷查询和追赶。
2. 复用现有认证、普通 token 文件写入、卷许可和租约。每 session 一次 Start 可携带多卷，保留 BuildOnce/BuildOrWatch。在状态消息中按卷带 `serial`、`generation`、`commit_seq`、持久游标/检查点。合并同卷通知时保留最新的同世代增量，GUI 仍独立核对磁盘。对于二次建立、立即同步或更改卷选择，协调当前 writer 或结束原 session 再起新 session，避免重复的 UAC 和并发写者。暂停等当前小事务结束后确认，恢复从 durable 继续，过期租约/退出取消所有卷任务并释放锁。
3. 可控阈值触发在线合并。冻结 U0 视图生成新基础，旧日志继续追赶。短提交区捕获 U1，把 U0 至 U1 的已持久批次重放到候选，flush、验前缀、切双槽后通知。读者通过共享代际锁保留旧世代，writer 仅在不属于有效恢复点且没有读者时回收。恢复遵循 STORAGE §7。发生断档、证据缺失、拒绝授权或卷离线时保留 last_good 并显式标记，追齐后恢复 Ready。

**验收**　C/D 或两个可控真实 NTFS 卷同时可搜，一个卷停更或故障时另一卷仍前进。验证暂停/恢复、无 UAC 读取已建索引、取消提权、单写者、日志断档、在线合并时查询和重启补追。复用 `ipc`、`permissions`、`storage-crash`、`storage-race`，新增用例只覆盖同世代追加/合并的实际缺口。测连续变更时的峰值内存、写盘量及可见延迟。退出后 worker 无残留。

### P4　补齐应用来源、启动与迁移

**涉及文件**　`rust/qbox-app/src/{shortcuts,search_service,activation,native_target,history_store,legacy_state}.rs`，`rust/qbox-engine/src/{catalog,legacy_import}.rs`，`rust/qbox-platform/src/windows/{shortcuts,shell}.rs`，新增 `rust/qbox-platform/src/windows/applications.rs`。对照 `src/QBox.Windows/Applications/{PackagedApplicationCatalog,PackagedApplicationActivation,ShellApartment}.cs`、`src/QBox.Windows/Indexing/IndexHistoryMigration.cs`。

1. 在 Windows Shell 线程枚举与 C# 相同的 AppX/注册应用来源，规范化 AUMID、名称和别名。接入现有应用目录匹配与排序，和普通 `.lnk`、代表 EXE、卷文件一起按基线合并。应用目录可后台刷新，刷新失败保留上次成功目录。零索引时仍可搜应用。
2. 启动按对象类型分发，文件/目录使用原生路径，`.lnk` 保留参数、工作目录和显示行为，AppX 走 AUMID 激活。以 C# 的识别和成功条件决定何时记历史，失败显示可理解的原因。
3. 将 T08 的 `import_legacy_state` 和旧索引导入接到桌面升级入口。对旧状态只读，先校验来源版本和历史目标，再在独立 Rust 目录分阶段写入并提交。显式导入、可重试、不会覆盖已有 Rust 数据。支持设置字段和启动覆盖项生效，包括索引路径、快捷键、自动更新、结果快捷键。旧 v1/v2 和旧 QBD 沿用已实现的导入；坏或日志不可追赶的卷提示重建。旧路径不重写为 Rust 存储。
4. 移植已索引目标的历史迁移。前后完整 FRN 相同且唯一可判定的改名/移动，可从旧 ID 原子迁移到新 ID，按原始名称身份比较。硬链接歧义、记录号复用、跨卷复制后删除都不能猜目标。利用已有历史锁与原子写，处理 GUI 和 CLI 并写。升级后历史既能读到，也能实际影响 P1 排序。

**验收**　对照冻结候选来源和完整 SearchHit；实机打开一个普通快捷方式和一个已安装 AppX，覆盖同目标不同参数、目录刷新、失败不学习。对旧状态副本走导入、重启、重复导入、失败重试、旧版回退，校验源文件哈希未变。覆盖历史唯一迁移、歧义保持和排序继承。

### P5　完成桌面功能对齐并核查后台资源

**涉及文件**　`rust/qbox-desktop/src/main.rs`（结果行或设置有独立职责时可拆 `rows.rs`、`settings_ui.rs`），`rust/qbox-app/src/{window_state,query,trial_settings}.rs`，`rust/qbox-platform/src/windows/{desktop_host,hotkey}.rs`。按需添加 `icons.rs`，对照 `src/QBox.Desktop/{MainWindow.xaml,MainWindow.xaml.cs,ResultRow.cs,DesktopSettings.cs}` 及 [DESIGN.md](../2026-11-17-纯Rust重写/DESIGN.md) §5、§7。

1. 补快捷框和主界面搜索流程。默认双 Ctrl 唤起时能直接输入，二次展开保留查询和选中目标，第三次隐藏。快捷最多 40，完整先 200 后一次续填到 2000。更多结果按钮/Alt+↓、窗口收起、可见选中项和列表滚动按 C# 行为。续填和后台刷新保留仍存在的候选选择，使用查询/快照版本阻止旧请求覆写。中文输入法组合期间 Enter 先提交文本。
2. 结果行显示图标、命中高亮、单行省略的路径与状态/常用提示，长路径可看全或复制。列表只绘制可见行及少量缓冲。图标异步限量并随查询或 DPI 变动失效，不阻塞首屏。支持方向键、Enter、单击、Esc、Ctrl+Shift+C，Alt+1–9 默认关闭且可配置。
3. 工整接通设置和托盘。自定义唤起键注册成功后才切换，冲突时保留旧键并提示。覆盖双 Ctrl 与其它组合键冲突、最小化/失焦/隐藏后的恢复、多显示器定位与 DPI、状态目录隔离的单实例及 `--start-hidden`。菜单可查看卷状态、立即同步、暂停/恢复、关闭自动更新和退出。桌面公开启动参数保留基线语义，旧私有 worker 开关在兼容表登记替代入口。
4. 后台记录 UI、查询、索引和 worker 的资源阶段。隐藏停止绘制、取消在途查询及无价值预热，回收有界可重建缓存；按基线空闲策略复核工作集。单独检查目前约 10ms 的热键轮询在 60 秒空闲窗口内的 CPU；超出 C# 基线再针对事件唤醒方案调整，并保留组合键语义。优先尝试剥离 Release 符号降低包体积，改动二进制时复测启动。图形栈替换仅在有数据表明阻碍对齐时单独评估。

**验收**　用独立 Release 包走默认及自定义唤起、打开与关闭、中文输入、改设置后重启、Alt+数字开关、复制路径、200/2000 续填和快速连续输入，确认新旧查询不能混显或误开。在普通 DPI 与至少一种非 100% 缩放检查操作。资源采集见 §3。

### P6　补全公开 CLI

**涉及文件**　`rust/qbox-cli/src/main.rs`（按需拆 `commands.rs`），`rust/qbox-app/src/{search_service,activation,history_store,index_build}.rs`，`samples/catalog.example.json` 用作对照输入。规范见 `src/QBox.Cli/CliApplication.cs`、[DESIGN.md](../2026-11-17-纯Rust重写/DESIGN.md) §9、[STORAGE.md](../2026-11-17-纯Rust重写/STORAGE.md) §9。

1. 补 `help`、`match`、`volume-probe`、`index-build`、`index-sync`、`index-info`、`search`、`open`、`recent` 与 Ctrl+C。保留 `--catalog`、`--index`、`--index-directory` 三类有效来源及 baseline 参数互斥、默认结果数、JSON 业务字段、stdout/stderr、退出码。旧 `--search-engine rust` 兼容为别名，显式 `csharp` 返回清晰错误。`import` / `import-state` 保留为独立迁移工具。
2. 索引命令走同一托管存储。新建的单文件逻辑入口使用 locator+相对 sidecar，同目录一起移动。旧 QBI/QBD 仍可只读查询。显式同步旧入口时先备份，写独立 store，验证后再发布 locator；失败保留旧入口。`index-sync` 日志断档按基线报重建，禁止静默全扫后返回增量成功。`search` 无历史写入，`open` 成功后才学习，直接使用应用层激活和数据逻辑。

**验收**　逐命令对照正常、坏参数/缺数据和副作用。隔离样例实证 search/catalog、旧 index、托管目录、open/recent、build/sync/info、断档及 Ctrl+C。文档登记有意差异，重点是新物理格式与纯 Rust 引擎选项。

### P7　正式候选、验收与切换

1. 新增 `scripts/publish-rust.ps1`，由干净 checkout 执行 `cargo build --manifest-path rust/Cargo.toml --release --locked -p qbox-desktop -p qbox-cli`。输出桌面 `QBox.exe`、单独 CLI、必要资源、许可证及升级/回退说明，记录每个文件的大小、哈希和版本。在源码树外用普通用户实际运行一次搜索、打开、导入及实时更新。有条件时在未安装 .NET 桌面运行时的 Windows x64 环境启动；登记桌面/CLI 进程和模块，确认用户路径纯 Rust。
2. 用 §3 的同一候选包做最终回归。补 `PROGRESS.md` 的功能矩阵，列出每项基线入口、Rust 入口、结果、证据、已知兼容差异。交付验证原始记录、包路径、构建命令、升级演练和回退路径。对阻断问题给出复现和归属任务。
3. 验收通过后，才在实施分支切换根 `README.md`、`DEVELOPING.md`、`CONTRIBUTING.md`、`ROADMAP.md`、`scripts/{dev,verify,publish,publish-desktop,check-repository}.ps1` 与 `.github/workflows/windows.yml` 的活动入口。列举实际被旧工程引用的默认命令、脚本和 crate 特性，清理产品构建中的 C# 活动工程、FFI、回退、sidecar 与旧依赖。冻结基线及发行历史作为归档保留。CI 验证 Rust 自动项和干净发布，实机 NTFS/IME/UAC 证据保留独立状态。用切换后的默认脚本再构建一次并烟测，确保新路径与验收的代码对应。

## 3. 最少验证与正式替换门槛

| 检查 | 何时执行 | 可接受结果 |
|---|---|---|
| 快速功能回归 | 各任务改完 | 跑受影响 crate 的已有测试及改动入口的一条真实路径。排序、来源或搜索热路径改变时跑七类查询。仅为已有检查抓不到的关键缺陷补用例。 |
| 一次单卷快速对照 | P2 出口 | 用代表性的宽查询、窄查询和中文/拼音查询各一条测首屏，顺手测新建/改名/删除可见延迟、实时更新的私有提交及写盘。明显回归先定位再扩到多卷。 |
| 一次候选包走查 | P7 | 在同一次运行中走通升级、唤起、搜索打开、双卷增量、暂停与恢复、CLI 和退出重启。复用受影响功能已验证的记录，其余在此补齐。用约 20–30 分钟的操作循环覆盖连续变更、显隐、主动触发一次合并和重启，记录是否出错、资源是否回落、worker 是否退出。 |
| 一次正式桌面对照 | P7 同一候选包 | 采首次可搜、热唤起、热查首屏、完整窗口续填、更新可见、隐藏后台及一次同步峰值。每条查询保留样本和结论，具体做法见下。 |

1. **只做一套正式短对照。** 同机、相同逻辑索引/历史/应用来源、两边均开实时更新，Release，轮流运行。快捷框 40 条测原七类、`1`、`python`、可命中的中文和拼音。主界面取命中足够多的一条，测 200 首批和续填 2000。默认 3 对交替短轮，每轮每条预热 1 次并记 5 次，冷进程首次结果和热唤起单列。报告原始值、中位数和最慢一次。P2 的三条诊断查询沿用同一取样口径，结果可直接定位 P7 出现的差异。
2. **用现有工具测真正的产品入口。** 优先用已有探针或两边同语义的少量打点。计时从应用接收输入到同一请求的布局完成。没有布局端点时，可以用同一方式观察两边首批结果可见并注明精度，内核耗时单列。隐藏后在相同时间点记录 GUI 加 worker 的私有提交、分进程工作集，并采同一空闲窗口的 CPU 时间。同步峰值和索引可见延迟各测一条实际路径。缓存或设备条件不可比的样本作废并说明原因。
3. **补测只追具体疑点。** 某条查询跨轮反复变慢且超过现场轮间波动，先修该路径，再用同样条件复测这条及受影响的查询。边界结果只加一轮定向复测，仍因噪声或端点缺测无法判断时标为未验证，报告给用户决定是否扩大样本。稳定出现资源增长、句柄不释放或间歇故障时才加长观察。完整结果对照、资源或时序专项可用 [VALIDATION.md](../2026-11-17-纯Rust重写/VALIDATION.md) 的现成工具。
4. **按发布风险留证。** 搜索速度在本次范围内无可重复退化，已索引变更及时可见，结果能打开正确目标，权限拒绝/重启能恢复，升级保留旧数据，退出无残留，才进入 P7 的入口切换。错开目标、丢更新、数据损坏、可复现搜索降速及关键路径缺测都要解决。保存一份 `PROGRESS.md` 加原始日志，列提交、包哈希、相同输入、命令和结果。fmt、clippy、workspace test 用已有脚本在最终候选集中跑一次。界面和权限走查用真实入口。百格矩阵、新统计工具、全量故障注入和长稳报告仅在简单检查暴露问题时选对应项目追加。

```powershell
# 从 Rust 实施工作树根运行，成功预期为退出码 0
cargo test --manifest-path rust/Cargo.toml --locked -p qbox-engine -p qbox-app
pwsh -File scripts/verify-rust.ps1
cargo build --manifest-path rust/Cargo.toml --release --locked -p qbox-desktop -p qbox-cli
```

**交付清单**　最终 EXE/CLI 与许可、功能对照表、性能和资源原始数据、真实卷与桌面操作证据、升级及回退文档、默认构建及 CI、清理活动 C# 依赖的检查记录。任务可分批提交，最终发布判断只看同一候选，且明确升级时旧版如何用保留的状态和原索引回退。

**工量预算**　沿用上一轮 16–28 有效人日作为规划区间。首先完成 P1 和 P2 的单卷短对照，随后根据多卷增量、后台内存和 Windows 实机问题修订估算。工量区间用于安排实现和验收，最终切换由上面的功能与性能证据决定。
