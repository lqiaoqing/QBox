# 文件迁移与逐项实施任务

状态：待执行。先读 README、DECISIONS、DESIGN、STORAGE、WORKER-PROTOCOL、VALIDATION。本文所有新路径/新测试是拟创建契约，不是已经存在。现有基线命令与新增命令分别列出。

## 1. 工作边界

从 `codex/feat-new@1f06daa0c9a9f29d8acbce357d169485afda92a4` 建立独立 `codex/full-rust-rewrite`，不在当前任务执行代码实施。基线分支若已前进，先记录差异并冻结提交。不得合并其他 worktree 的试验。

允许修改：`rust/**`、新增 `scripts/*-rust*.ps1`、本目录；T18 才将根 README/DEVELOPING/CONTRIBUTING/ROADMAP、默认 verify/dev/publish 脚本和 Windows CI 切换到 Rust。C# 源码、原夹具与原验证脚本在 T00 冻结后只读，最后按已过验收的迁移表移除活动工程引用。基线探针可在独立副本增加测量字段，但必须保留 diff 并证明不改搜索/存储逻辑。

不修改：正式索引/设置/历史、已有用户文件、其他 worktree、无关未跟踪文件、既有 release 档案。新构建与大型证据仅放忽略跟踪的 `work/`，不提交私有索引和个人路径。

## 2. 源文件到目标模块映射

以下目标子目录可按内部组织调整，但一项都不能以“重写 UI 已完成”代替移植。

| 原文件/组（仓库根相对） | Rust 目标 | 必须保持的语义 |
|---|---|---|
| Core/Matching/FuzzyMatcher、MatchResult | engine/fuzzy.rs | 分值、CanMatch/Score/Match 一致、高亮 |
| Core/Matching/PreparedApplicationName、Data/pinyin.tsv | engine/pinyin.rs、rust/assets | 字面优先、全拼/首字母/半拼、许可和资源路径 |
| Core/Search/SearchCollector、LauncherOrdering、SearchEngine | engine/search.rs、pipeline.rs | 桶、混合前十、稳定平局、别名与总数 |
| Core/Search/LatestQueryGate、SearchProgress | app/query.rs | 最新输入、取消、静默刷新、完成判定 |
| Core/Search/SearchChrome | app/window_state.rs | Compact/Results/Full/Hidden 与 40/200/2000 |
| Core/Search/SearchResultActivation、SearchActions | app/activation.rs | 同输入静默刷新可打开、单击拖动判断、启动成功才学习 |
| Core/Input/DoubleControlDetector | app/input.rs | 时间阈值、左右 Ctrl、组合键打断、计时回绕 |
| Core/History/SelectionHistory、HistorySnapshot、FileHistoryPersistence | app/history_store.rs、engine/history.rs | 关联排序、限制、时间、锁、冲突、原子保存 |
| Windows/Indexing/VolumeIndex、MappedIndexStorage、VolumeIndexStore | engine/index/{model,qbi,store}.rs | 原始文本/身份/引用校验、QBI v3 与映射生命周期 |
| Windows/Indexing/NamePresenceIndex、PhoneticNameIndex、NameTextPool | engine/index/{presence,phonetic,pool}.rs | 预筛不漏候选、名称共享策略和准备次数 |
| Windows/Indexing/LiveVolumeIndex、LiveIndexFile | engine/index/{overlay,snapshot}.rs | 不可变视图、删除覆盖、父链与 last_good |
| Windows/Indexing/IndexChangeLog | engine/index/{delta,manifest,generation}.rs | 新日志、连续检查点、发布与恢复 |
| Windows/Indexing/NtfsIndexBuilder、NtfsChangeReader | engine/index/{builder,change_reader}.rs | 缓存核对、追赶、父链修复、去噪、日志缺口 |
| Windows/Indexing/IndexHistoryMigration | app/history_migration.rs | 单一路径迁移、DOS 别名与硬链接歧义 |
| Windows/Indexing/SearchHotPathWarmup、VirtualMemoryPrefetch | platform/windows/prefetch.rs、app/warmup.rs | 前台/后台切换、可取消、不重复全量准备 |
| Windows/Ntfs/MftRecordParser、MftAttributeListParser、UsnJournalParser、NtfsControlBuffers | engine/ntfs/{mft,attributes,usn,control}.rs | 字节布局、USA 两种输入模式、边界和序号 |
| Windows/Ntfs/INtfsVolume、JournalCheckpoint | engine/ntfs/{source,journal}.rs | 可重放来源接口、日志连续性 |
| Windows/Ntfs/NativeNtfsVolume、MappedNtfsStream、NtfsFileResolver、NativeFileIdentity | platform/windows/ntfs、engine/ntfs/{runs,resolver}.rs | 原始 IO、稀疏/多 extent、完整 FRN 与取消 |
| Windows/Shortcuts/*、Applications/* | platform/windows/{shortcuts,applications,shell_sta}.rs | 来源顺序、LNK 参数/目录、AUMID、刷新保留 |
| Windows/ShellTargetLauncher | platform/windows/launch.rs | 原始路径/参数、应用启动、身份再核验 |
| Desktop/HotkeyRegistration、ShellIconLoader | platform/windows/{hotkey,tray,icons}.rs | 注册失败保留旧热键、低延迟 hook、图标生命周期 |
| Desktop/DesktopSettings、App、MainWindow、ResultRow | app/settings.rs、desktop/src/{main,ui,rows}.rs | 设置/单实例/渲染/输入与激活，不复制核心算法 |
| Desktop/IndexUpdateJob、LiveIndexWorker、IdleWorkingSetReclaim | app/worker、platform/windows/{pipe,token,working_set}.rs | 提权、租约、单写、退出与空闲策略 |
| Cli/CliApplication、Program | cli/src/{main,commands}.rs | 每命令参数/副作用/退出码与数据隔离 |
| Windows/Search/RustSearchEngine、EngineHistoryBlob、EngineCatalogSnapshot；Rust/ffi.rs | 离线对照保留后移除产品引用 | 用 Rust 类型替代，不再构建/加载 cdylib |
| tests/QBox.Checks/*、Fixtures | rust/tests/fixtures + engine/app/platform tests | 逐项登记 ID，对照数及留出样本，不只迁移部分 |
| tools/EngineBench、MemoryProbe、DesktopProbe | rust/tools/qbox-validation | 真实实现计时、重放与 UI 完成证据 |

表中 Core、Windows、Desktop、Cli 分别是 `src/QBox.Core`、`src/QBox.Windows`、`src/QBox.Desktop`、`src/QBox.Cli` 前缀；Rust 目标 crate 名以 `qbox-` 开头。平台 crate 依赖 engine 定义的模型/来源 trait，engine 不依赖平台实现；app 依赖两者，禁止 crate 环。

## 3. 开工与验证命令

现有命令，必须在冻结基线环境核对：

```powershell
pwsh -File scripts/verify.ps1
cargo test --locked --manifest-path rust/qbox-engine/Cargo.toml --lib
cargo run --locked --release --manifest-path rust/qbox-engine/Cargo.toml --bin engine-harness
```

注意：当前 GNU linker 为 crate 相对路径，旧 Cargo 命令若受 cwd 影响，应在 `rust/qbox-engine` 执行等价命令并记录；不能静默改全局 PATH 或使用另一版本冒充原构建。新 workspace 构建需在 T01 解决自身独立性。

T00/T01 创建 `rust/tools/qbox-validation`（bin=`qbox-validation`，仅开发验证，不进产品包）。后文 `V <suite>` 的精确展开是：

```powershell
cargo run --manifest-path rust/Cargo.toml --locked --release -p qbox-validation -- <suite> --manifest work/rust-baseline/baseline.json --output work/rust-validation/<suite>.json
```

`--manifest` 对自足格式测试可缺省；依赖基线/实机的 suite 不得缺省后假通过。命令/字段在 VALIDATION 定义；每个 suite 是必须实现的任务，不是现有占位入口。新增自测的 `cargo test` 可按 test 名过滤，但最终必须跑全 workspace。

## 4. 实施任务

每项按“先检查/失败用例 → 最小实现 → 本项验证 → 性能/相邻回归 → 更新进度 → 提交”执行。不要一次提交整个重写。下面为可审核任务单；其内部的小函数与单元用例可进一步拆分，不改变契约。

### T00 冻结功能、测试与性能输入

依赖：无。新增基线副本和 `work/rust-baseline/baseline.json`；建立 `rust/tests/coverage-map.json` 的初始清单。

步骤：核对分支与源码摘要 → 构建固定 C# 程序和探针 → 执行原检查 → 导出全部实际检查 ID/预期/夹具摘要 → 捕获同一逻辑时刻的基础/日志/应用目录/历史副本 → 校验可加载与最终检查点 → 完成 A/A 校准。原始私有数据不得提交。

通过：685 匹配和全部行为检查逐项登记；现有 2950 行为记录如不符先解释差异，不抄 README 旧数。每个基线程序/输入有 hash，复制的 qbi/qbd 绑定通过，无变化的目录枚举不能偷偷重取为另一份对照。

### T01 Workspace、资源和 GUI 选型原型

依赖 T00。新增 5 个产品 crate、验证工具、Cargo.lock、`rust/assets`；先保留旧产品源码只读。固定工具链/后端。拼音资源嵌入或随包固定定位，不读取 C# 源码目录。

先测 GUI 输入法、DPI、焦点、hook/托盘接入、真实搜索结果虚拟列表和资源曲线，再扩大界面；先做隐藏/显示的负载与断连退出检查。`cargo test`、`V gui-spike`，手工输入法证据不可由 mock 替代。框架关键项不通过，先停止 GUI 扩展并记 ADR；可继续纯核心任务。

### T02 原始文本与数据类型

依赖 T01。engine `model.rs`、`text.rs`；无损 U16Text/NativePath/TargetId/Checkpoint/HistoryTime 与显示索引映射。

先增加代理对、孤立项、特殊大小写、Unicode 与时间边界、完整 FRN 序号的失败样本，再迁入类型。`V text` 必须验证原始单元往返、真实打开参数不来自有损显示、旧 JSON 日志转义的无损导入可行性。

### T03 直接 Rust 搜索与顺序对照

依赖 T02。迁移 fuzzy/pinyin/search/pipeline、资源及夹具；建立直接 API，保留原来源遍历与桶规则，不并行重排。

`V behavior`：685/36/2058 的既有对照加覆盖映射；逐轮 full SearchHit 对照，不使用 FFI JSON 缺字段的旧输出作为完整对照。未知基线行为先建 characterization，不修改排序兜底。

### T04 查询协调器、窗口状态与历史

依赖 T03。app/query.rs、window_state.rs、history_store.rs、activation.rs。先用可控屏障模拟快慢请求/取消/世代切换，验证最新待执行槽和 200→2000 衔接；再接真实引擎。

`V query-state`、`V history`：取消在途、重复打开、静默刷新可激活、历史写冲突/时间、隐藏清空、退出 join。关键任务的回收计数和进度事件必须可观察，不能仅测最终字符串。

### T05 搜索性能前置准入

依赖 T03/T04。`V bench-core`，依 VALIDATION 固定全部查询、limit 和 static/live。不等第六阶段再分析。越线用采样剖析区分预筛/评分/collector/物化/分配，只优化等价路径。未通过保留原数据，不能删慢查询或换基线。

### T06 QBI v3 与 QBD v2 编解码

依赖 T02。engine/index/{qbi,delta,model}.rs。逐字段实现 STORAGE；先负向格式样本再读写。`V format`：P0/P1、动态头、未知版本、重算摘要的非法填充、帧截断/损坏、溢出和无损文本。

### T07 世代、锁、提交与恢复

依赖 T06。engine/index/{manifest,generation,store}.rs 与平台锁/文件发布。先在隔离目录、普通权限实现，不连原始卷。

`V storage-crash` 在每个持久化点注入进程中断；`V storage-race` 用屏障保证新读者、旧快照、第二写者和 GC 并发。400 次保存中验证 reload/apply/GC 实际覆盖；不能增加测试壳私有重试隐藏写失败。恢复后完整文件模型与检查点一致。

### T08 旧数据导入与迁移

依赖 T06/T07。新 importer 和 CLI 导入命令。旧 QBI/QBD 及设置历史只读；保留原文件/元数据，输出到不同目录，再导出逻辑数据、UTF-16 和检查点对照。

`V import`：v1/v2、共享名称、旧 JSON、半尾帧、坏完整帧、绑定不符、历史冲突、失败不覆盖、重启。不能声称无法无损导入时历史自动清空；必须明确失败/重建策略及验证结果。

### T09 原始卷接口、worker 管道与权限

依赖 T01/T07。platform/windows/{pipe,token,fs,ntfs}、app/worker。先实现真实管道身份校验与普通用户缓存 IO，再实现受控读取；按 WORKER-PROTOCOL 不让提升线程直接写任意缓存路径。

`V ipc`、`V permissions`：假进程/假 pipe、长帧、断连、心跳超时、取消阻塞 IO、路径/reparse 竞态、第二写者、普通 token 拒绝写管理员专有隔离位置。权限前置不过不进行真实全盘初建。

### T10 NTFS 初建与实时追赶

依赖 T06–T09。移植 parser、runs、resolver、builder、change_reader；先固定记录重放，后真实卷。不得以递归文件枚举或普通 watcher 替换原 MFT/USN 能力。

`V ntfs-replay`、`V ntfs-real`：USA 两模式、多 extent、FRN 复用、硬链接、DOS 别名、父链、扩展记录、日志断档。真实文件仅在本次隔离目录新建并清理；还需实际初建/重启 worker，不能只查冻结索引。

### T11 索引与更新性能准入

依赖 T10。`V bench-index`；201001 条记录+2000 改名重放、保存/加载/追赶/追加/合并/重新映射逐阶段对照。同一 GC 控制条件，物理磁盘单列。worker 与 GUI 共同运行时，更新可见延迟和搜索尾延迟不退化。

### T12 应用来源、Shell 与历史迁移

依赖 T04/T10。移植 LNK/AUMID 枚举、启动/工作目录、图标、目录刷新及历史迁移。Shell apartment 统一线程所有权，失败保留旧目录。

`V catalog`、`V activation`：命令参数/空参数、真实独立样例应用启动、失败不学习、成功后历史失败不重复启动、普通快捷方式与打包应用、改名/移动/硬链接歧义。避免打开用户的现有业务程序作测试。

### T13 完整 GUI/CLI 接入

依赖 T04/T12、T01 原型通过。desktop 完整窗口和 settings，cli 原命令逐项兼容。GUI 仅绑定 app，不另实现排序/索引。

`V gui-state`、`V cli`：双 Ctrl、设置、单实例、托盘、选择、复制路径、更多结果、IME、DPI、旧图标取消、后台状态。所有支持的 CLI 命令分别核对参数、输出、退出码与副作用；不是只运行 search。

### T14 GUI 性能准入

依赖 T13。`V bench-gui`：输入与结果 request/revision 对应、首屏/续填/隐藏后唤起；同一工具测 C# 与 Rust。正常窗口资源、worker 开关和 GPU 成本分别报告。IME/物理操作证据补全，不用固定等待制造响应数字。

### T15 综合性能与稳定性

依赖 T05/T11/T14。`V bench-all`、`V soak`。固定统计判定和补测上限；反复查询、取消、显隐、快照切换、UAC 拒绝、worker 退出及至少一次 8 小时资源观察。无确认退化且所有必需项目有效，才允许清理旧工程。

### T16 验证工具反向测试

依赖 T00 起逐步实施，T15 前完成。`V validator-negative`：空输入、缺文件/缺一轮结果、错结果、慢项、跳过集成、坏时钟、测量不完整必须导致失败/不确定，不能绿。仅套件函数存在但没有执行真实用例，覆盖清单检查必须抓出。

### T17 干净发布包与环境隔离

依赖 T15/T16。新增 publish-rust.ps1，产品只包含 Rust GUI/CLI、需要的资源与许可。在独立干净 checkout 构建；普通运行无需安装 .NET，无额外 C# 进程/DLL；拼音和夹具路径不依赖原工程。

`V package`：检查依赖与实际进程模块，加载复制索引、迁移副本、查询、启动隔离目标、正常退出。不能仅扫描文件名后断言已独立。

### T18 移除活动旧架构并交付

依赖 T17。根据映射移除旧活动 C# 工程/FFI/回退和默认构建依赖，保留冻结基线离线文件与历史归档。更新根开发入口、默认 verify/dev/publish 和 Windows CI；新增 GUI/实卷无法在 CI 执行的项目保持独立状态，不伪装自动完成。

从全新 checkout 运行默认构建/验证/发布，再跑行为、数据、集成和综合报告。交付提交、包 hash、报告、原始证据入口与 BLOCKED；不自动合并、push 或替换正式版。

## 5. 接续与失败处理

PROGRESS 每条记录任务 ID、状态、输入/输出摘要、实际命令、退出码、提交与下一步。同一原因连续失败三次，先定位、更换验证方法或推进无依赖任务，不连续重跑等待偶然成功。设计变更必须写 DECISIONS，标明哪些旧结果失效。

性能与系统验证不确定时保留完整代码/证据在重写分支，不删除原实现、不宣称可替换。需要新取舍的阻塞单列，不能借此停止所有独立任务。最终交付不能把原型通过、自动检查通过或自检通过当作独立复核通过。
