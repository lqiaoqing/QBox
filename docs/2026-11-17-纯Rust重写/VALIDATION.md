# 验证输入、测量与判定规范

状态：待实现契约。本文方法冻结于代码优化前；若发现方法错误，先用反向用例证明、记录 ADR，并使受影响两组全部重测，不能只改候选结果。

## 1. 输入与覆盖清单

T00 创建 `work/rust-baseline/baseline.json`，至少含 schema_version、固定源码提交、基线程序/探针 hash、工具链与构建 flags、机器/电源/系统、进程参数、root 目录、全体输入文件相对路径/长度/SHA256、每卷身份/基础检查点/增量最终检查点、应用目录与历史摘要、测量配置及检查 ID 清单摘要。

基线主组为 feat-new 默认 C#；当前 Rust 混合组可记录但不替代主组。不得将 0.2.5 档案 EXE 与另一份 C# 源码探针混称同一版本。同一套完整 SearchHit 输出协议用于直接 Rust 与 C#，测量边界一致。

新 `rust/tests/coverage-map.json` 每项包含：id、源文件/展开用例名/序号、对应 Rust test 或 validation suite、期望摘要、平台条件、执行证据。C# 动态生成的每个用例展开登记，不能只登记外层循环；名称重复时以源位置与展开序号消歧。685 ASCII 包括 holdout，2950 行为按 T00 实际输出核对；7/8 个 Rust 顶层 test 不能代表全部覆盖。

原 fixtures 和新冻结预期的 hash 固定。迁移可改测试驱动，不可改预期适配新错误行为。缺项、重复 ID、没有真实执行证据、skip/todo 都不能覆盖通过。已确认基线缺陷单列 KNOWN_DEFECT 与替代预期理由，不能借此修改用户认可的排序。

## 2. 查询、结果与可重放数据

性能核心查询固定为：

```text
1, CODE, python, 豆包, Any, 800Q, .code, nd, Tra, chatgpt, rtt,
资料, 中, 任意中文不存在探针, doubao, qianwen, 吕, 𠀀, a中1, aa, aA
```

static/live × limit 40/200/2000 全覆盖；静态仅相同基础，live 为相同最终逻辑文件、冻结应用/快捷方式与历史。新格式转存不要求文件 hash 与旧格式相同，但必须另有原始记录/文本/身份/检查点等价证明。空查询、limit=0、错误输入属于行为检查，不混作正常 UI 搜索性能。

每轮比较完整 total 与 displayed items，序列严格相同，字段至少包括 ID、原始 UTF-16 名称/目标、kind、score、group、normal_priority、application/direct flags、arguments、working_directory、AUMID、完整 UTF-16 高亮 ranges 和 history signals。Null 与空字符串不等价。所有 u64/i64 用十进制字符串，原始 UTF-16 用 u16 数组或 LE bytes 的 Base64，禁止有损 JSON 字符串掩盖差异。

旧 JSON 解析必须保留 `\uD800` 等孤立单元。已有读库无法做到时，写无损 string-token 适配或拒绝该导入，不能默认替换为 U+FFFD。数据转换报告应覆盖整个文件集，不只比较几个查询的 total。

确定性索引重放沿用 201001 records+2000 USN 改名，保存原始输入与事件顺序、所有阶段逻辑文件/检查点摘要。新增/删除/移动/硬链接等使用固定种子模型测试扩充；实盘数据对照需要同一冻结时刻，不能让两组各扫一次正在变化的磁盘后比较 total。

## 3. suites 与必要覆盖

验证工具提供 `--suite` 或 MIGRATION 所列位置参数形式；最终固定一种并使脚本一致。本文采用位置参数 `qbox-validation <suite> --manifest ... --output ...`。必需项目由 validation-plan.json 列出，不由本次输出里出现哪些文件决定。

| suite | 最小覆盖与完成证据 |
|---|---|
| text / behavior | 原始单元、时间/ID、685/36/2058 对照及其余行为映射 |
| query-state / history | 可控线程屏障、取消在途、静默 Fill、快照切换、激活/持久化冲突 |
| format / import | STORAGE 全部正负格式，旧数据与坏尾帧，无损往返、源文件不变 |
| storage-crash | 每个 write/flush/发布步骤前后中断；恢复点、尾部增量及文件模型一致 |
| storage-race | 至少 400 次提交；reader 在 writer 开始前就绪；至少 100 次受控重载与 20 次 GC 交错；无未预期写失败 |
| ipc / permissions | 身份、帧、租约、取消、普通 token 写入、路径/reparse 与进程残留 |
| ntfs-replay | 两种 MFT 编码、runs/extent/硬链接/FRN/父链/断档等原检查逐项对应 |
| ntfs-real | 新建隔离目标的初建、增删改移、父目录移动、worker 重启、真实更新可见性 |
| catalog / activation | 来源、参数、工作目录、打包应用、打开身份、成功后学习、失败不重复启动 |
| gui-spike / gui-state | IME、焦点、DPI、双 Ctrl、托盘、单实例、40/200/2000、旧图标取消 |
| cli | 每个既有有效命令的参数/退出码/输出/副作用；不能只测查询 |
| bench-core / bench-index / bench-gui / bench-all | 本文各性能单元均有足量配对样本及结果一致证明 |
| soak | 至少一次 8 小时，显隐/查询/取消/发布/重启循环，无泄漏趋势或遗留 worker |
| validator-negative | 缺文件/轮次/字段/错误结果/可控慢项/skip 必须检出 |
| package | 干净构建/安装路径、资源/许可、运行模块、真实查询/启动/退出，无 .NET 依赖 |

每次取消用开始屏障确保搜索已进入执行，取消到任务结束的延迟单列。每次释放循环先确认任务在途再发退出；验证最终 Arc/句柄/线程均回收。单元 fake VolumeSource 合法，但不能充当 ntfs-real 或实际 GUI 通过。

## 4. 时间边界与采样

统一使用单调高分辨率时钟，原始单位 ns。跨进程 GUI 事件用同机 QPC 和频率转换，不能用本地日期时间差。每轮记录请求 ID、输入 epoch、snapshot revision、limit、total 和结果摘要。

| 指标 | 起点 → 终点 |
|---|---|
| load/prepare | 打开/验证/映射单列；构建实际搜索派生结构单列，不藏进预热 |
| core search | 调用实际 Search API → 完成 collector 与结果物化；另列 scan/finish，不能遗漏末尾解析/物化 |
| input-to-rows | UI 接受已提交输入 → 正确 request/epoch 的可激活结果模型安装完成 |
| input-to-layout | 相同输入起点 → 含该结果模型的 UI 布局/帧构造完成；不是 GPU present 或物理像素 |
| wake | 唤起事件到达 → 窗口激活/可输入，另测含新查询的正确结果完成 |
| update-visible | 受控变更操作完成 → 查询对应新身份/删除状态可见 |
| save/append/compact | 实际业务入口 → 本文定义的 durable 完成及必要重映射 |

C# 基线在独立冻结副本加最小测量钩子（保留 diff/hash），不得修改匹配、缓存、等待策略；Rust 用相同语义钩子。计时不包括测试工具寻找 UI 元素的耗时；两边都在应用收到输入时打点。若只取得 UIA/Dispatcher 端点，就如实标端点，不能用不同定义作快慢结论。

不能用“有列表项”、旧请求、固定 sleep 或 400ms 轮询稳定等待作为本次完成。布局端点、模型端点和物理输入法/双 Ctrl 的人工操作分开记录。未取得某个必需端点则不确定，不能拿核心耗时代替 GUI 响应。

初始每单元 5 个独立进程对，AB/BA 交错，查询每进程 first 一次、预热 2 次、计时 10 次；进程内查询顺序按固定种子生成，每一对使用相同顺序。first 单列；未清文件缓存不得称物理冷启动。综合时每查询/模式/limit 独立判定，不能用总体均值抵消慢项。

bench-index 的首轮冷/JIT 单列，其后每组至少 5 个进程的相同输入重放；两组 GC 控制选项一致。新 Rust 不需要 GC，但不能拿强制 GC 的 C# 探针内存对比自然 GUI 驻留。物理磁盘初建另报告相同卷/逻辑工作量/缓存条件，不把缓存重放称磁盘吞吐。

## 5. 零退化统计判定

这里将“不允许确认退化”实现为严格门槛，不新增 5% 或 0.5ms 宽限。统计不能证明无限场景中的数学相等；未能排除退化只能是不确定。

1. A/A 使用同基线新进程对和同方法。校验结果一致、没有组别固定偏差、时钟分辨率合格。A/A 接近零的区间应覆盖零，不把它要求为“候选更快”；若 A/A 出现显著方向偏差，先修测量环境，不进入 A/B 优化。
2. 每单元 primary 指标：各进程 timed-run median 再取 median；P95 为等样本数进程 pooled timed runs 的 nearest-rank `ceil(0.95*n)-1`。GUI 同理，至少 3 个会话/组、每会话 20 次唤起，内存至少 3 轮。
3. 计算候选减基线的绝对差（ns/bytes/CPU ns），不用基线接近零时会失真的比值。对所有必需 primary 单元及其 median/P95 联合建立同时置信区间。
4. 固定层次 bootstrap：以 AB/BA 进程对为块重采样；块内各组 timed runs 分别重采样；每次计算所有单元差。使用固定 ChaCha8 seed=0x51424F58，50000 次；按下列公式构造近似同时区间，覆盖所有单元，不用每项 95% 冒充全体 95%。统计实现必须有独立合成数据和慢项反向校验。
5. 三次预定分析时点为累计 5、10、20 个进程对；每次使用新增独立、相同协议样本，合并之前全部有效样本，不丢首轮。每次分配 alpha=0.05/3，同时区间置信水平为 1-alpha，控制重复分析。GUI/内存补测按会话/轮组等比例增加。所有原始尝试留存。
6. 完整数据、结果一致，且全部必需单元差值区间上界<=0 才判性能通过。任一单元下界>0 判确认退化；修实现后作为新候选重新整组测量。区间跨零为不确定，到预定上限仍不确定则结束补测并阻塞替换。

公式：单元 j 的原始统计量差为 d[j]，第 b 次重采样差为 d_star[b,j]。令 s[j]=max(bootstrap 差值的样本标准差, 该指标实测分辨率)，z[b]=max_j(abs(d_star[b,j]-d[j])/s[j])，q=max(1,nearest_rank_quantile(z,1-alpha))。同时区间为 [d[j]-q*s[j],d[j]+q*s[j]]。quantile 统一用升序位置 ceil(p*n)-1。时钟分辨率取 QPC tick 转换值，CPU 用采集 API 的分辨率，内存用采集粒度；不能全设 0。不同量纲先标准化，不直接对 ns 与 bytes 取 max。first 指标只重采样进程对；内存/GUI 按会话块重采样，不把同会话页面样本当独立进程。

上述是 bootstrap 近似而非分布无关的绝对保证，须在 T00/T16 完成统计原型验证。工具说明公开公式、有效块和固定合成测试预期；若发现方法错误，先记录修订并重测两组，不能根据是否易通过选择方法。

first、加载/准备、输入到结果、更新可见性、索引阶段与资源同样必测，不可只用预热 core search 作为整体准入。单次原始 first 是描述值，阶段准入采用跨进程 first 分布的 median/P95；样本不足由同样补测规则处理。

## 6. CPU、内存和稳定性

CPU 单独无重型采样运行：进程树 user+kernel CPU 时间，固定墙钟窗口；空闲窗口至少 60 秒，确认没有扫描、重绘或热键轮询。分别测可见静止、隐藏、worker 无变化、受控更新和搜索。CPU 百分比同时标逻辑核数，不作为唯一原始量。

内存低成本 PrivateUsage/WorkingSet 采样间隔 20ms 用于峰值下界，记录实际间隔；QueryWorkingSetEx/VirtualQueryEx 重型驻留分类仅在阶段点执行，不放进搜索耗时窗口。每组记录 startup 5/9/12/16/22 秒、正确 python 查询完成后的可见状态、隐藏 2/12/20 秒。GUI/worker 单列且报告总体私有提交；共享物理页不简单相加。GPU 本地/共享内存单列，采集接口不可用时标缺证据，不能填 0。

常驻阶段与峰值分别比较，不能用降低一个抵消另一个确认退化。空闲回收保持两组同条件，没有额外 GC/工作集裁剪。至少一次 8 小时稳定性记录 CPU、提交、句柄、线程、缓存/快照数量与循环次数；资源持续增长即定位，不能依靠定期重启通过。

## 7. 报告 schema 与退出码

每 suite 输出 JSON schema_version=1，至少：run_id、suite、source_commit、binary hashes、baseline manifest hash、config hash、start/end、environment、required_case_ids、executed_case_ids、各 case status/reason/evidence、metrics 原始样本及区间、进程树、清理结果、fail_count、uncertain_count、skip_count、overall。

status 仅 pass/fail/uncertain/not_run；not_run 的必需项目总体 uncertain。输入空、少一轮、缺结果字段、缺时间端点、无实机权限、进程提前退出、样本数不足都不是 pass。新增 fake 数据可以测统计工具，但不能填充真实基准样本数。

退出码：0=全部必需检查通过，1=明确失败，2=不确定/必需证据缺失。运行单 suite 仅形成局部报告；最终聚合器按冻结计划检查所有 suite ID 和输入摘要，不能仅扫描目录中已有报告后判全绿。

验收脚本实际调用构建、test、验证与基准工具，逐一检查退出码。可选 skip 只用于局部开发，最终聚合必须阻止总体通过。源码/夹具/阈值 hash 变化让旧结果失效；报告不得覆盖上一候选证据，使用 run_id 独立目录。

## 8. 验收工具的反向用例

- 空输入、缺必需 suite、缺进程对/某轮查询、重复 case ID、旧 commit/hash、未知 schema：明确 fail/uncertain，非零退出。
- 结果 total 相同但一个高亮/参数/目标变化：必须 fail。
- 某个宽泛查询加入固定延迟而其余变快：必须检出慢项，不能由平均值抵消。
- 相同候选重复采样且噪声覆盖零：允许 uncertain，不能默认 pass。
- 隐藏空查询测试不出现新 request：不能登记为正确查询完成。
- reader 重载计数为 0、取消全在执行前、释放全在完成后：对应压力项 fail。
- suite 内工具失败后后续成功、或跳过 GUI/实卷：最终不能返回 0。

最终独立复核应重跑必需命令并抽查未参与实现的原始案例；执行者自检不等于已合并或已获正式替换批准。
