# 索引格式、提交、锁与恢复协议

状态：待实施协议 v1。多字节整数均为 little-endian；长度运算必须 checked。底层 API 封装归 platform，格式与状态机归 engine。普通 GUI/CLI 只读索引，worker 是单写者。不得原地修改已发布 QBI。

## 1. 文件组织及身份

```text
indexes/<volume-serial-16hex>/
  writer.lock
  manifest-a.qbm
  manifest-b.qbm
  generations/
    <uuid-32hex>/
      readers.lock
      base.qbi
      changes.qbd
```

世代 UUID 随机且不复用；manifest 的 commit_seq 从 1 单调递增，溢出即拒绝提交。新目录不代表已提交，只有有效 manifest 指向的状态才可对外发布。根目录及文件写入的普通用户权限要求见 WORKER-PROTOCOL。

`base_file_sha256` 是完整 QBI（含末尾摘要）的 SHA-256；QBI 末尾摘要则只覆盖正文，两者不得混淆。日志头绑定 base_file_sha256、卷、世代、journal 与基础 next_usn。

## 2. QBI v3 精确布局

沿用 `QBXNTFS1` 与 v2 名称共享语义；不修改名称池去重策略。头部按顺序写入，无结构体隐式 padding：

| 字段 | 编码 |
|---|---|
| magic | 8 bytes，`QBXNTFS1` |
| version | i32=3 |
| root | .NET BinaryWriter 兼容：7-bit 无符号 UTF-8 字节长度，随后根路径 UTF-8；本轮为固定 NTFS 卷根 |
| serial | u64 |
| sector_size, cluster_size, record_size | 各 i32 |
| mft_valid_length, mft_start_lcn | 各 i64 |
| major_version, minor_version | 各 u16 |
| journal_id | u64 |
| next_usn, created_at_ticks | 各 i64 |
| node_count, name_count, text_count | 各 i32；text_count 为 UTF-16 单元数 |

根字符串不能硬编码头长 92；7-bit 长度最多 5 字节、必须在 i32 正范围内，root 字节长度上限 1024；非法 UTF-8、非本轮支持的根语法拒绝。使用源码的真实 NtfsGeometry 完整往返，不能沿用当前 Rust 简化 geometry 后丢字段。

Node 固定 28 字节：

| 偏移 | 类型 | 字段 |
|---:|---|---|
| 0 | u64 | 完整 FRN |
| 8 | i32 | first_name |
| 12 | i32 | name_count |
| 16 | u8 | is_directory，0/1 |
| 17 | u8 | has_usn，0/1 |
| 18 | i64 | last_usn，无值时 0 |
| 26 | u16 | link_count |

Name 固定 17 字节：parent_node:i32 @0、text_offset:i32 @4、text_length:i32 @8、namespace:u8 @12、attributes:u32 @13。text_offset 与 length 均以 UTF-16 单元计数，不含 padding。

设 H 为头结束位置，T=H+28*node_count+17*name_count。写 P=(2-T%2)%2 个零字节，再写 text_count*2 字节原始 UTF-16LE。最后追加 SHA256(所有此前字节)，32 字节。全文件长度必须恰好等于 T+P+2*text_count+32。

读取顺序：最小头/版本 → 计数与长度 checked 核对 → 正文摘要 → 零填充 → 节点/名称引用及共享片段合法性 → 映射可搜索视图。保留现有根、FRN 顺序、父链、DOS 别名、名称引用、共享文本完整覆盖等校验；不允许仅验证摘要。

Node/Name 非对齐字段用显式读数；禁止直接把 17/28 字节记录 cast 为有对齐要求的 Rust struct。文本必须确认地址两字节对齐后借用；v3 mmap 基址与偏移均满足要求。旧 v1/v2 导入允许临时对齐副本，但新基础卷查询不能保留此副本。Owned overlay 的副本和基础映射分开计数。

## 3. 新 QBD v2

新日志不再使用 JSON 保存文件名。旧 `QBOXDLT1`（40 字节头+JSON 校验帧）只作为只读导入输入；保留其原身份计算与半帧规则，不能用 v2 规则解析旧日志。

新头共 84 字节：magic[8]=`QBOXDLT2` @0，version:u32=2 @8，base_file_sha256[32] @12，serial:u64 @44，generation_uuid[16] @52，journal_id:u64 @68，base_next_usn:i64 @76。

每帧：`payload_len:u32 | payload | SHA256(payload)[32]`。payload_len 上限 64 MiB，下限 28；超限在分配前拒绝。检查点前进但无名称变化时可合并至最多 60 秒再写空帧；有变化/准备合并/正常停止时立即覆盖到待提交检查点。observed 只用于内部追赶，durable 才能对外作为持久位置；崩溃从 durable 重读无变化区间不能丢名称变化。

payload 顺序：

1. `sequence:u64`，从 1 连续递增；`from_usn:i64`；`next_usn:i64`；`change_count:u32`。
2. 每项：`tag:u8`（0 删除、1 完整 upsert）；`file_frn:u64`。
3. tag=1 时：`is_dir:u8,has_usn:u8,last_usn:i64,link_count:u16,name_count:u32`；随后逐名称 `parent_frn:u64,namespace:u8,attributes:u32,name_units:u32,name_utf16le[name_units*2]`。

解析后必须正好消费 payload，无尾随字节；booleans 只允许 0/1。计数受 payload 剩余长度限制。完整文件可以有多个有效硬链接名称；名称顺序和去重遵循基线，不通过普通 Unicode String 往返。

首帧 from_usn=base_next_usn，后帧 from_usn=上一 next_usn，next_usn>=from_usn；同帧同记录号只允许最终一个变更。记录号被复用时新 FRN 替换旧身份；删除携带旧 FRN，不能误删复用后的新记录。序号连续和 USN 连续都检查，不能只查其一。

writer 在帧写完并 FlushFileBuffers 成功后才发布其 durable_cursor；读端只应用完整校验帧。读端本次 Refresh 先捕获文件长度作为 read_limit，只解析到该边界，不因持续追加无限追读；边界内不完整尾帧不消费，保留上个完整 cursor 待下轮重读。发布承诺的 prefix 必须完全位于 read_limit 内，否则该槽不能成功安装。每次批处理 checkpoint 与变更共同提交，不能分别发布。

## 4. Manifest 双槽

每槽 envelope：magic[8]=`QBXMAN01`、payload_len:u32、UTF-8 JSON payload、SHA256(payload)[32]。最大 payload 64 KiB；严格类型、必需字段及路径语法，不接受重名 JSON key。摘要验证原始 payload，不重新序列化再验。

payload 必需字段：

```json
{
  "version": 1,
  "commit_seq": "1",
  "generation": "0123456789abcdef0123456789abcdef",
  "serial": "0000000000000001",
  "journal_id": "1",
  "base_next_usn": "100",
  "base_file_sha256": "64 lowercase hexadecimal characters",
  "log_prefix_len": "84",
  "log_prefix_sha256": "64 lowercase hexadecimal characters",
  "published_next_usn": "100"
}
```

示例 hash 是字段说明，不是合法测试文件。u64/i64 用十进制字符串（serial 固定 16 位 hex），避免诊断工具浮点精度损失。generation 仅 32 位小写 hex，不能传任意文件路径。文件位置由固定布局推导。log_prefix_sha256 覆盖发布时 QBD 的 [0,log_prefix_len)；后续合法追加不改变此摘要。

两槽均验证 envelope 和所引用文件：选择完整有效且 commit_seq 最大者。相同序号引用不同世代视为损坏，不随意二选一。新加载必须验证已承诺 prefix 完整可解析且到达 published_next_usn，再读取后续完整帧；prefix 内缺失不能当尾部半帧忽略。

有效上一槽可以提供旧完整视图，但状态为 Degraded，告警需追赶，不能冒充最新。已持有较新 last_good 的进程不能因槽损坏倒退到旧 checkpoint。

## 5. 锁与打开规则

worker 启动后对 writer.lock 字节 [0,1) 取得独占 LockFileEx 锁，保持到停止；第二写者非阻塞失败为 WriterBusy。不能只依赖锁文件存在。

每个 SnapshotOwner 对对应 readers.lock 字节 [0,1) 持有共享锁，Arc 的最后持有者释放时才解锁。进程退出由系统释放句柄锁。reader 使用 OPEN_EXISTING，不能创建世代目录或锁文件。读者顺序：读取两槽 → 尝试共享代际锁 → 打开基础与日志 → 重新核对槽/世代有效性 → 校验并安装。拿不到锁或文件已被回收则重读 manifest，不能在 UI 忙循环；已有视图继续用，首次加载重试最多 3 次后报告。

回收器只处理未被两份有效 manifest 引用且不在 active staging 集合的世代，非阻塞取得代际独占锁后再次核对 manifest，才删除本世代内固定文件。writer 在创建候选目录时持代际独占锁直到提交状态确定，发布后释放并通知读者；GC 不能把正在构建但尚无 manifest 的目录当孤儿。

reader 无锁时读取 manifest 不是使用承诺；GC 恰好先完成则 reader 重新定位。回收器删除数据后留下锁文件墓碑；不在本轮运行中删除/重建锁文件，以免产生两个同名锁对象。启动维护可在持 writer 锁、确认非恢复点和 staging、取得代际独占锁并确认只剩锁文件后清理目录；reader 从不创建文件，失败后重定位，UUID 不复用。任何 reader 都禁止凭旧路径重建已删除数据。

manifest/日志读句柄允许 READ|WRITE|DELETE 共享；基础只读映射不允许应用代码写。manifest 读句柄短持有；锁只锁专用文件，不把锁区当内容 IO。LockFileEx 调用须封装正确的同步/异步完成语义及 UnlockFileEx，不能把 IO_PENDING 当作已获得锁。

## 6. 初建与合并提交顺序

初建视为无旧世代的合并。全量构建最多一个，旧世代仍可读。

1. 在单写者内选取 durable 检查点 U0，固定包含 U0 前全部变化的只读视图；创建唯一 staging 世代。后续批次继续写旧日志。
2. 用冻结视图生成 QBI v3，写完摘要、flush 并重新验证。writer 的候选校验通过持有独占锁的 unpublished 内部接口完成，不能调用会再次获取共享锁的公开 load 造成自锁。新基础的 next_usn=U0；created_at 为新基础创建时间。导入仅改格式时保留旧 created_at。
3. 暂停“日志提交”而非阻塞 UI；取得 old durable U1。将 U0 后到 U1 的完整变化按原顺序写入新 QBD，从 sequence=1 重新编号；保留 from/next 连续性，不把正在读取但未提交的通知计入 U1。
4. flush 新日志，核验 prefix 摘要、连续性、最终 U1 及新视图逻辑结果。短提交区内不继续向旧日志追加；其后 USN 仍由系统日志保留，恢复后从 U1 继续。若 pause 太久造成 journal gap，取消候选并重建，不能发布 Ready。
5. 写下一 commit_seq 到较旧/空的 manifest 槽的同目录临时文件，flush，原子发布（已有槽 ReplaceFileW，首次槽同目录 rename）。不先删旧槽。更新内存 active_writer 到新世代，然后从 U1 追赶。一次只允许一个发布者。
6. 发布失败：不能立即恢复向旧日志写入，先重新读取两槽判定是否实际生效；新槽有效就接管新世代，否则继续旧世代。若无法确定，停止写入并报故障，保留可读 last_good。
7. 发布成功后通知普通读端；只有新世代完成加载准备后交换 Arc。回收队列只清不再被任一有效槽和 reader 锁保护的世代。

维护上一槽是恢复点，不要求其持续接收新世代后续变化。若回退到它，必须以其 checkpoint 重新追赶 USN。不得把“还有旧基础”当成没有丢更新。

## 7. 恢复矩阵

| 中断/损坏位置 | 启动或继续运行行为 |
|---|---|
| staging QBI 未完成 | 旧槽继续；候选无提交资格，后台回收 |
| QBI 完成但日志/prefix 未完成 | 旧槽继续；不能靠扫描目录选更新 UUID |
| 新槽临时文件半写 | 忽略临时文件，选择有效槽 |
| Replace 已完成、内存尚未切换 | 读取槽识别新提交，恢复新日志 durable cursor，继续追赶 |
| 发布后额外日志尾帧不完整 | 只消费此前完整帧；writer 独占状态下修复尾部并从对应 USN 追赶 |
| 完整帧摘要坏/断序/检查点跳跃 | 不跳帧；已加载进程保留 last_good，worker 取有效前缀重建候选或回退有效恢复点并追赶 |
| prefix 内不完整/错误 | 对应槽无效；尝试另一槽并标 Degraded；不能宣称 Ready |
| 两槽无效 | 首次导入或从真实卷重建；运行中不清掉仍可用的内存视图 |
| journal_id 变化或 checkpoint 出界 | 旧视图 Degraded；独立重建，不能假装增量追齐 |
| GC 失败/读者长期持锁 | 保留文件并告警；不阻塞查询和当前提交，不能强行取消外部 CLI |

回收默认保留两个恢复点及所有活跃读者。达到“恢复点与读者之外额外 2 个待回收世代”的默认上限时暂停新合并、继续增量并报告；磁盘空间低到无法安全追加则停止写入，不能无限重试/耗尽磁盘。

flush/rename 的具体掉电保证依赖 Windows、文件系统和硬件缓存。必须验证进程中断恢复；若未做断电/虚拟机掉电测试，只能声明已过进程崩溃恢复，不声称绝对断电持久。

## 8. 必须通过的格式与并发用例

覆盖动态头长、P=0/1、旧格式导入、共享名称、孤立代理项、硬链接、FRN 复用、前后检查点、半帧/坏完整帧、未知版本/标志、溢出计数、两个槽冲突、失败后重新判定提交、持旧映射与 GC、新读者/GC 竞态。用固定种子随机操作序列与内存参考模型对照全部逻辑文件，再查关键查询。

Windows API 参考：[LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex)。实际 flags、共享方式和 API 成功/失败语义必须由平台测试确认。

## 9. CLI 索引入口 locator

为保留 `--index <路径>` 而不另造单文件覆盖协议，新写出路径采用 locator：magic[8]=`QBXLOC01`，payload_len:u32（最多 4096），UTF-8 JSON，SHA256(payload)[32]。payload 为 `version:1, store_dir:<单文件名>.qbox-store, serial:<16hex>`；store_dir 只允许同父目录下一个普通名称分量，拒绝点段、分隔符、冒号和 reparse。实际卷目录为 `<入口父目录>/<store_dir>/<serial>`，内部使用前述双槽布局。入口由应用验证根约束，不信任任意路径转发。

`index-build --index x.qbi`：先创建独立 sibling store、完整提交初建世代，最后同目录临时写/flush/原子发布 locator，命令才成功。入口发布前崩溃只留下未引用的候选目录；没有“有效入口指向空 store”的状态。已有入口转换时不得覆盖不相符的 store_dir，发生冲突明确失败。

`index-sync` 读取 locator 后操作同一 managed writer。旧 QBI 转换：在入口专属锁下捕获基础与完整日志、验证绑定并导入新 store，先保存源 QBI/QBD 的只读备份及摘要，再原子替换入口为 locator；同名旧 qbd 不删除，locator 解析从不读取它。入口替换前后分别是完整旧输入与完整新 store。转换过程中旧读取者继续持原映射，新的 reader 根据 magic 分派；瞬时打开失败走已记录的有限生产重试（20/50/100ms，最多 4 次尝试），耗时/失败均记入验收，不加测试壳私有重试。

读入口按 magic 区分 legacy QBI 与 locator；CLI 用户指定的 managed 内部 base 路径是只读诊断输入，不能更新。移动 locator 必须同时移动 sibling store；缺失 sidecar 明确报错，不回退创建空索引。需要单个可移动 QBI 时，另行导出一次性完整 QBI v3（含当前增量物化后的检查点），它是只读交换文件，不承担实时更新发布协议。
