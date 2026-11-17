# 数据模型、接口与执行状态

状态：待实现契约。以下新增类型与路径均不是已存在的产品代码。所有默认预算见 DECISIONS；验证编号见 VALIDATION。

## 1. 公共类型

跨层只传拥有明确所有权的 Rust 值，不传裸映射指针或 C 线格式。禁止将 `String` 作为 Windows 文件名/路径的无损容器。

| 类型 | 必需字段/含义 |
|---|---|
| `U16Text` | `Arc<[u16]>`；比较、匹配、序列化使用原始单元；不要求合法 Unicode |
| `NativePath` | Windows 为 `OsString`；平台外只通过接口打开，显示不反向生成真实路径 |
| `TargetId` | 原有稳定 ID 的无损编码；NTFS 序列号/完整 FRN/父 FRN/名称摘要规则保持基线 |
| `FileIdentity` | 平台命名空间 + 卷身份 + 完整文件身份；Windows FRN 保留高 16 位序号 |
| `GenerationId` | 16 字节随机 UUID；不是查询版本、USN 或 manifest 序号 |
| `Checkpoint` | `journal_id:u64, next_usn:i64`；语义为下一次应读取的位置 |
| `SnapshotStamp` | app 单调 `revision:u64`，包含卷世代/增量游标、目录版本和历史版本 |
| `SnapshotSet` | 已准备的卷视图、应用/快捷方式目录和历史快照；持有所有映射及代际锁 |
| `QueryRequest` | `request_id:u64, input_epoch:u64, text:U16Text, limit:u32, purpose, snapshot:Arc<SnapshotSet>, cancel` |
| `QueryResponse` | 请求/输入/快照版本、原查询、完整 `total:u64`、有序拥有型 `Vec<SearchHit>` |
| `SearchHit` | TargetId、显示名称、真实 NativePath、类型、参数、工作目录、AUMID、优先级、分数、组、历史信号、高亮、激活身份 |
| `TextRange16` | `start:u32,length:u32`，以 UTF-16 单元计数；不能改成 UTF-8 字节位置 |
| `HistoryTime` | UTC 的 .NET ticks（自公元 1 年、100ns 单位）以 i64 无损保存；不截断为毫秒 |
| `CancelFlag` | `Arc<AtomicBool>`；取消只作用于本请求/任务树，读 Relaxed，线程退出仍由 join 和所有权保证 |

`request_id` 每发起一次任务递增；`input_epoch` 仅在输入、窗口模式或主动替换查询时递增。同输入的 2000 续填与快照静默刷新可以产生新 request_id，但不使已经正确展示的当前结果立即失效。

取消、权限拒绝、格式损坏、日志断档、卷离线、启动失败、历史写冲突、资源不足分别有错误类型。`Canceled` 不是引擎故障；不捕获一切错误后返回空结果。公开边界返回 `Result`，不以 panic 处理损坏输入。

### UTF-16 与显示

名称/路径的身份、摘要、匹配与磁盘数据不做 Unicode 归一化。有损转换仅发生在显示适配器：输出显示 UTF-8 和每个原始 UTF-16 单元对应的显示字节区间，代理对合并映射，孤立代理项显示替代符但保留底层单元。高亮通过映射投射，不能直接用单元下标切 UTF-8。复制路径和 Shell 打开使用原始宽字符路径。

保持基线的 Ordinal/OrdinalIgnoreCase、拼音表、桶合并及 stable ordinal，不使用会展开多个字符的通用 lowercase/uppercase 替代。对 ASCII、特殊大小写、补充平面字符和孤立项逐项对照。可执行文件识别与 WindowsApps 排除策略先留在 Windows 来源适配器，不能强塞到未来 macOS 名称规则。

## 2. 模块 API 契约

接口名称允许内部实现调整，参数语义、所有权和副作用不变。测试使用实现接口的确定性来源；实机验收必须走真实平台实现。

| API | 输入 → 输出与副作用 |
|---|---|
| `SearchEngine::search` | `&QueryRequest → Result<QueryResponse>`；同步、可取消、无磁盘写入、不学习历史 |
| `SnapshotStore::load` | 卷目录 + cancel → 持有代际锁的基础与有效增量；校验失败不替换 last_good |
| `SnapshotStore::refresh` | 旧快照 → 新 Arc 或 Unchanged；成功前不修改旧快照 |
| `GenerationWriter::commit` | 带连续检查点的变更批次 → durable cursor；严格单写者 |
| `GenerationWriter::compact` | 冻结视图 → 新世代描述符；协议见 STORAGE |
| `VolumeSource::geometry/bounds/read_record/read_journal/enumerate` | 几何/日志/原始记录；没有搜索排序及持久化写入 |
| `IndexBuilder::build/synchronize` | VolumeSource + 基础/检查点 + cancel → 完整闭合视图，未提交的结果 |
| `CatalogService::refresh` | 原目录 → Unchanged/新目录/保留旧目录的告警；STA 系统枚举不阻塞 UI |
| `ActivationService::activate` | 当前显示行 + input_epoch → 平台校验和启动；启动成功后异步提交历史 |
| `HistoryStore::read/record/migrate` | 加锁读取，摘要 CAS 写入；冲突重读并按原规则合并，最多重试 3 次（含首次共 4 次尝试），再失败则报告 |
| `PlatformShell::launch` | 原始路径、参数、工作目录或 AUMID → Submitted/Failed；不是应用已完成运行 |

历史限制保留当前每目标最多 8 个关联查询、最多 500 条最近查询；归一化、前缀、时间排序、冲突合并规则逐项迁移 `SelectionHistory`。NormalizeQuery 保留 Trim+ToLowerInvariant 语义。设置和历史只由普通 GUI/CLI 写，worker 不写历史。单 GUI 串行写入；CLI 与 GUI 的写冲突使用跨进程锁+内容摘要，不能覆盖另一写者的新值。锁等待保持基线 3 秒，放弃锁的异常按已取得锁处理但重新验证磁盘文件。

### 设置与历史线格式

保留现有 JSON version=1，避免仅为重写另造业务格式。Settings 字段为 version、indexDirectory、hotkey、autoUpdate、resultShortcuts；默认分别为 1、null、DoubleCtrl、true、false。History 为 version、targets、recentQueries；targets 项含 targetId、lastUsedUtc、queries[{query,lastUsedUtc}]，recentQueries 项含 query、displayText、lastUsedUtc。字段大小写、null/空与合法性依旧有夹具核对，未知版本拒绝并保留源文件。

时间按原 DateTimeOffset 的 ISO-8601 精度读写，内部 ticks 不丢 100ns；查询/显示/目录字符串由无损 UTF-16 JSON string 适配器处理，必要时逐单元输出 `\uXXXX`，不能先交给只接受 Unicode scalar 的 String 再序列化。TargetId 与关联时间的排序、同目标合并均取自原实现。

历史写入为“持锁读摘要 → 检查预期摘要 → 生成完整新内容 → 同目录临时写/flush/原子发布 → 更新内存摘要”。冲突重试前重新读并合并，写失败不先更新内存。改名迁移与 RecordSelection 使用相同原子边界。

## 3. 线程与队列

共享模型必须自然满足 Send+Sync，禁止照搬 FFI 句柄上的 unsafe Send/Sync 声明。现有 RefCell 查询缓存迁为请求局部值；共享派生预筛使用 `RwLock<Option<Arc<PreparedSearch>>>` 与独立 prepare 串行门，构造完成才安装。查询先克隆 Arc 再释放锁，扫描期间不持写锁；取消的半成品不安装。空闲清理只清缓存槽，已在执行的查询仍持有效 Arc；下次准备在后台线程完成，不阻塞 UI。底层数据视图不可变，缓存清理不改变逻辑 revision。

本轮使用 std::thread 与 crossbeam-channel 的 bounded/select 队列，不默认引入通用异步运行时。取消对象除 AtomicBool 外持有唤醒通道：CancelScope 拥有唯一 sender，取消先设 flag 再关闭 sender，克隆 receiver 全部被唤醒。扫描热路径只查 flag；等待队列/阻塞发送必须同时选择取消通道，不能在队列满时无限等。图标/进度使用 try_send 与合并策略，业务启动请求超时明确失败。

| 执行者 | 工作 | 队列与退出 |
|---|---|---|
| GUI 主线程 | winit/eframe 事件、状态机、渲染、版本核对 | 不扫描/等待 join/做 COM；后台通过 event-loop proxy 唤醒 |
| QueryCoordinator | 拥有最新快照、输入与执行状态 | 一个正在执行 + 一个可替换 pending；pending 被替换立即丢弃 |
| 搜索线程 | 调用 SearchEngine，持有 Arc 快照 | 同时一个 GUI 查询；不同 CLI 进程互不取消 |
| SnapshotLoader | 加载、校验、准备新视图 | 单槽合并通知；仍在加载时记录 dirty，结束后最多补载一次 |
| Shell STA | 系统目录/图标/启动等需要 apartment 的操作 | 有界 64，图标请求可丢弃/去重，启动请求必须明确成功或失败 |
| 平台消息线程 | keyboard hook、RegisterHotKey、托盘消息 | hook 仅更新小状态并入队，始终 CallNextHookEx |
| worker 卷任务 | 原始卷读取、日志追赶、单写发布 | 每卷一个写者；全量初建/合并最多一个重任务，事件/取消可及时处理 |

无忙轮询；无后台 60fps 动画。时间任务仅为租约、目录刷新、现有日志追赶和 10 秒空闲策略。取消检查至少在每 256 个扫描名称/每批文件记录、循环排序和物化边界执行；该频率是默认实现参数，T05 实测取消延迟后调整但不能降低正确性。每卷最多由 current、一个在途查询和一个加载候选持有三份世代准备状态；pending 指向 current，不排队保留历代快照。显示行和图标任务拥有自己的启动/路径数据，不因列表显示长期钉住整份旧快照。

## 4. 查询调度状态机

状态：Idle、Running、RunningWithPending、Stopping。所有完成事件回到协调器，执行线程不直接改 UI。

| 事件 | 动作 |
|---|---|
| 新输入/模式变化 | input_epoch++；取消 running；用新请求替换 pending；旧显示按原激活策略禁用 |
| 输入清空 | 取消 running，清 pending/结果，显示空态；空输入不全盘搜索 |
| running 完成/取消 | 先按当前 request_id/epoch 核对；旧响应丢弃；再启动最新 pending |
| 正确 200 结果完成 | 一次发布列表与总数；Full 且更多结果时 enqueue Fill(2000)，保留当前显示可操作 |
| 快照/目录刷新 | revision++；原输入非空且可见则排静默刷新，合并重复通知；隐藏不搜索 |
| 较新快照到来时旧查询完成 | 未再有输入且旧快照仍可用时可临时展示，标记刷新中并排新请求；有新输入则丢弃；不能冒充最新版本验收完成 |
| 隐藏 | epoch++、取消、清查询/结果/pending，停止图标及前台预热；索引工作继续 |
| 退出 | 进入 Stopping，停止接收、取消所有任务，再后台 join；不得 drop 仍被任务引用的映射 |

Fill 使用首屏相同的快照、历史和输入 epoch。若这期间数据发生变化，取消 Fill，改在新快照上重查目标 limit；不能把两份快照的行拼接。请求之间只复用只读预筛和限额缓存，不复用带旧查询的 collector。

### 行激活

可激活：查询文字与 input_epoch 相同、有结果、未在重新加载；同输入的静默刷新/Fill 不阻断已显示行。每行拥有完整启动数据，不保留悬垂 name_index。打开前检查 FileIdentity/快捷方式/应用仍有效；旧 FRN 序号不得打开复用后的另一文件。单击抬起同一行且未拖动/未命中滚动条才打开；Enter 与单击共用防重复的 activation_id。

启动成功定义为平台提交成功，此后即使取消搜索仍学习历史；历史写失败显示独立告警，不把已启动当作未启动而重试打开。

## 5. 窗口与系统交互

| 状态/输入 | 新状态与动作 |
|---|---|
| Hidden + 双 Ctrl/托盘打开 | Compact；清输入，放到当前显示器，恢复焦点 |
| Compact/Results + 再次双 Ctrl | Full；保持查询 |
| Full + 再次双 Ctrl | Hidden |
| Compact + 非空输入 | Results，limit=40、可视 8 行 |
| Results + 清空 | Compact |
| Full + 输入 | 保持 Full，先 200 后 2000 |
| Full + 收起按钮 | 有查询 Results，否则 Compact |
| 快捷模式失焦 | Hidden；Full 失焦保持显示 |
| Esc/关闭按钮 | Hidden；只有托盘退出真正结束进程 |

DoubleControlDetector 保持首次释放到第二次释放间隔 <=400ms、每次按下 <=300ms、重复按下忽略、组合键/其他键打断、无符号计时器回绕。原 `ControlGestureChecks` 全部迁移。RegisterHotKey 使用 MOD_NOREPEAT；注册新键成功后才注销旧键。用户偏好的其他工具组合键本轮仅保留命令注册接口，不实际占用。

Windows hook/托盘使用独立隐藏消息窗口，和 eframe 窗口 HWND 生命周期分离；消息经 event-loop proxy 交主线程。不得从 hook 回调启动搜索或进行磁盘 IO。IME 组合期间不把 Enter 当作打开结果，组合完成后按已提交文本发请求；是否组合由真实 IME 事件驱动，不能仅延迟固定毫秒数猜测。

列表按稳定 TargetId 做选择保留，只绘制可见行及少量 overscan。图标缓存按(目标身份, 图标版本, DPI)键，路径缓存按(世代, delta revision, name_index)键；父链变化或失效时不沿用旧路径。默认预算见 ADR-07。

## 6. NTFS 建库与增量算法

逐项移植，不把“调用某个 MFT 库”当作行为等价。接口边界取自 `INtfsVolume`，记录解析器输入为显式字节缓冲区。

1. 初建读 journal bounds，记录 start=(journal_id,next_usn)。解析 `$MFT` 的 data runs，按几何参数及 checked 运算读取，默认沿用 64 条记录/批。
2. 原始记录验证 FILE 签名、USA/fixup、属性边界；系统 FSCTL 返回的逻辑记录单列编码模式，不重复应用盘面 fixup。坏盘面记录按现有策略用 ReadRecord 重试，不静默跳过。
3. 完整解析 ATTRIBUTE_LIST、扩展段与主记录身份；只有主记录进入索引。合并不同 extent 的名称和数据，循环/缺段/序号不符均明确报错。
4. 使用系统缓存枚举核对仍存在的记录；普通记录不在缓存视图中则删除，低编号元数据按原策略处理；多链接/USN 或名称变化重新 resolve。
5. 从 start 追赶 USN，修复缺失父链；不可达名称按已有逻辑重核对，保留其他有效硬链接，父节点为普通文件则拒绝。
6. 检查 checkpoint 的 journal_id 相同且 `max(first_usn,lowest_valid_usn) <= saved_next <= current_next`；超界或日志实例变化触发重建，不能标为 Ready。
7. 增量按记录号合并通知，但完整 FRN 决定最终身份；刷新扩展记录时转主记录；查询期间替换的是完整文件记录/删除标记。
8. 仅数据写入造成 USN 变化而名称、类型和链接无变化时，不重建名称视图，允许空变更检查点帧；避免索引写入自激循环。
9. 后台合并不负责历史猜测；应用层对前后单一有效路径、FRN 不变的目标迁移历史，DOS 别名不等同额外硬链接。

保持基线每卷约 1 秒追赶、一般故障 30 秒重试、ChangedRecords>=10000 触发合并的初始策略。无名称变化的日志位置只在最多 60 秒时提交空帧；分别维护 observed_next 与 durable_next，新的有变化帧从 durable_next 开始覆盖期间已确认的无变化区间。不得每秒 flush 空帧造成后台 IO 退化；进入合并前先把 observed 检查点持久化，再按 durable U0 冻结。

存在日志缺口时保留 last_good 可搜索视图，状态为 Degraded/Rebuilding；新基础追齐并发布才恢复 Ready。重建不能在 UI 线程运行，卷故障重试默认 30 秒；权限拒绝不自动重复触发 UAC。

## 7. 生命周期及资源

启动按状态目录取得单实例锁，再加载设置、历史、应用目录和快照。试用默认独立 QBoxRust 状态目录，只有显式导入才读取旧 QBox 状态。无索引时仍允许应用搜索；不创建假文件命中。加载失败展示具体来源状态，保留其他可用来源。

单实例键为用户 SID+规范化状态目录的 SHA-256，名称加 QBoxRust 前缀；普通 GUI 持有命名 mutex 到退出。同实例使用普通权限的 show pipe，只接受版本化 Show 请求（最大 4 KiB），DACL 限本用户与 SYSTEM，并核对服务端进程身份。第二实例仅发送 Show 后退出；未知版本/不同产品不能当成功唤回。管道暂未就绪时短重试最多 3 次，明确报启动中或故障，不能启动第二个 GUI。它不复用管理员管道，也不能发送索引/执行命令。

目录默认每 30 秒刷新，快捷方式通知可提前合并触发；失败保留旧目录。文件快照发布通知可通过 worker 管道唤醒；无 worker 的独立查询按目录元数据低频探测，不重复哈希不变文件。索引发现只枚举托管卷目录及显式旧平面入口，不递归把 generations/备份/sidecar 当作额外来源；同一卷存在不同有效入口时明确报重复身份，不双计数或静默选任意一份。

隐藏 10 秒后取消预热、释放可重建缓存，并按基线策略回收工作集；显示后重新准备热页不能阻塞 UI。行为与资源验证分别包含回收前后，不能用裁工作集代替释放缓存。

启动/退出顺序：先停止接收新命令 → 取消查询/加载/图标 → 向 worker 发 Shutdown → 保存普通用户状态 → 后台等待任务与句柄释放 → 托盘/hook 注销 → 进程退出。超过退出预算记录哪个任务未响应；不能以 `mem::forget`、脱离后台线程或伪造完成解决生命周期问题。

## 8. 跨平台及扩展

Windows 类型只出现在平台模块内部；公共层不假设盘符、分隔符或 FRN。macOS 文件发现、应用与权限另设实现；没有实机只标记公共层编译结果。未来工具使用 `ToolId + Command` 与独立状态服务，快捷键路由到工具服务，搜索不拥有截图/OCR/翻译的重资源。

## 9. CLI 兼容边界

必须登记并实现：match、volume-probe、index-build、index-sync、index-info、search（catalog/index/index-directory）、open（同三类来源）、recent，以及 Ctrl+C 取消。保留 `samples/catalog.example.json` 的 version=1 候选输入。

新 CLI 仅 Rust 引擎；兼容接受旧 `--search-engine rust` 为无作用别名，显式 csharp 返回清晰的不支持错误，不能启动旧程序。此为删除旧架构后的有意变化，单列兼容报告；不是保留双实现。

index-directory 采用 STORAGE 世代协议。`--index file.qbi` 保留命令参数语义，但新建索引路径是 STORAGE 定义的 locator 入口，不继续原地覆盖映射中的大文件。所有更新都通过相同 managed store，不建立另一套单文件发布/恢复算法。托管目录内的 base.qbi 不能通过单文件 sync 修改。

旧 QBI 仍可只读查询并读取绑定的旧日志；需要 sync 时先导入 sibling managed store，成功后将入口转换为 locator。原 QBI/QBD 必须备份并保留；这个显式 CLI sync 转换只允许在用户指定的非正式隔离输入上验证，正式切换另行授权。普通 search 不自动覆盖旧路径。directory 发现旧平面 qbi/qbd 时同样只读导入独立 managed 目录，源不变。物理写出格式变化单列兼容报告，不声称旧 C# 能读取新入口。

`search` 不写历史，`open` 提交启动成功后才写。index-sync 的日志断档仍要求重建，不能悄悄全扫后当作成功增量。CLI JSON 输出与旧产品业务字段保持，新增诊断字段版本化；参数/退出码逐命令从冻结基线捕获，文档不猜测未核对的错误码。
