# Windows 工作进程协议

状态：待实施 v1。仅用于本机索引控制与状态，搜索仍在普通 GUI/CLI 内直接调用 Rust。原始卷只读；不创建/删除系统 USN 日志，不运行任意 Shell 命令，不写目标文件内容。

## 1. 启动与身份

1. GUI 按状态目录取得单实例锁，生成随机 session UUID，保留自身 PID、进程创建时间和真实 EXE 文件身份；在后台线程使用 ShellExecuteExW/runas 启动同一 EXE。
2. 参数只含固定模式、session、parent PID/创建时间及管道名；不是可执行命令字符串。不在 UI 线程等待 UAC。保存返回的 worker 进程句柄，后续校验 PID 和创建时间，不能仅看同名进程。
3. worker 作为管道 server 创建 `\\.\pipe\QBoxRust.<session>`：FIRST_PIPE_INSTANCE、REJECT_REMOTE_CLIENTS、双向 byte 模式、overlapped IO；DACL 只允许父进程用户 SID、SYSTEM 和 worker 自身身份，不使用 Everyone。伪造同名 pipe 导致明确启动失败，不连接其他服务。
4. GUI 连接时设置 SECURITY_SQOS_PRESENT 和 SECURITY_IMPERSONATION（不使用 delegation），随后用 GetNamedPipeServerProcessId 与刚启动的实际进程核对；worker 用 GetNamedPipeClientProcessId 与保留的父进程 PID/创建时间核对。双方验证 EXE 文件身份与用户信息；session 是路由值，不当作身份认证秘密。
5. Hello/Welcome 完成版本、session、父/worker 创建时间核对。未知主版本或身份失败关闭连接且不打开原始卷。worker 启动后 10 秒内未认证连接则退出；用户等待 UAC 的时间不计入此 10 秒。
6. 启动被拒绝：GUI 为 PermissionDenied，保留只读搜索与明确重试入口；不自动再次请求 UAC。父进程退出或管道断开使 worker 取消。

按 session 区分试用和正式状态目录，测试不得唤回原正式实例。单实例机制与索引 worker 是不同锁，不用同一全局取消标志控制所有实例。

## 2. 权限与目录写入

原始卷读取需要提升权限，但缓存、日志和索引文件写入不需要提升权限。选择如下线程分工：

- worker 在认证连接上 `ImpersonateNamedPipeClient`，取并复制父 GUI 的普通用户 impersonation token，然后 RevertToSelf；失败即退出。
- 专用 CacheWriter 线程在任何文件操作前装入该普通 token，整个文件 IO 生命周期保持 impersonation；不在可迁移的 async task 中依赖线程 token。
- 所有锁文件、基础文件、日志、manifest、临时文件、GC、缓存目录操作只允许经过 CacheWriter；断言线程 token 为父 GUI 的非提升身份。失败不能退回 admin token 继续写。
- 读取原始卷由独立 RawVolumeReader 使用提升身份完成，解析结果经有界队列交给写入端；此线程不接受普通文件删除/覆盖命令。
- GUI 配置的 index root 先验证普通用户可读写；本 session 固定一个 root，若要切换先停止 worker 再启动新 session。文件名全部按 STORAGE 推导，不接受每条消息提供任意路径。
- 打开 root/世代目录后保留目录身份，拒绝 reparse 根与越界路径。子项使用已持有的 RootDirectory 句柄逐个单名称分量 NtCreateFile，并设 FILE_OPEN_REPARSE_POINT 后核对 tag，拒绝 reparse、点段与路径分隔符；不把含多个不受信任分量的字符串当作安全相对名。不得只做字符串 StartsWith 检查，也不依赖未验证的 OBJ_DONT_REPARSE 组合语义。发布/删除包装必须保留同目录与身份约束。目录替换/reparse 竞态必须有测试；即使路径校验失效，普通 token 也不能写入管理员专有位置。
- 不递归删除未知目录。GC 只删除自己生成、已验证 UUID 世代内的固定文件；遇到未知文件或 reparse 点停止清理并报告。

此设计避免把文件路径校验作为唯一提权边界。执行者须根据锁定 Windows API/bindings 版本完成 token、目录句柄和相对 IO 封装及反向测试；不能以文档描述代替实测。原始卷只允许启动请求中枚举并验证的本地固定 NTFS 卷。

## 3. 线格式

每条消息 header 固定 24 字节：magic[4]=`QIPC`，major:u16=1，kind:u16，payload_len:u32，sequence:u64，correlation:u32。整数 LE，payload 为 UTF-8 JSON，最大 1 MiB；先验长度再分配。sequence 每发送方向从 1 严格递增，correlation 为请求号，0 表示无关联通知。未知 kind、重名 JSON key、重复 sequence、未知版本断开并记协议错误。

所有 UUID 固定 32 位 hex；所有可能超出安全整数精度的 u64/i64 字段用十进制字符串；byte path/UTF-16 不通过任意 JSON 字符串混传。错误字符串只用于展示，不作为逻辑分支。

| kind | 方向 | 必需 payload 字段及动作 |
|---:|---|---|
| 1 Hello | GUI→worker | session,parent_pid,parent_created,protocol=1；发完才允许其他请求 |
| 2 Welcome | worker→GUI | session,worker_pid,worker_created,capabilities；认证完成 |
| 3 Start | GUI→worker | job_id,index_root,volumes,mode(BuildOrWatch),config_version；每 session 仅一次有效 Start |
| 4 Ack | 双向 | correlation,state；表示接收/状态转换，不表示索引已经保存 |
| 5 Heartbeat | GUI→worker | session,heartbeat_seq,idle；维持父任务存活 |
| 6 SetPaused | GUI→worker | paused:bool；暂停读取/提交，完成正在提交的小事务后 Ack |
| 7 SetIdle | GUI→worker | idle:bool；控制预热/10 秒空闲回收，不停止日志追赶 |
| 8 Cancel | GUI→worker | job_id；停止任务，最终发 Stopped |
| 9 Shutdown | GUI→worker | reason；取消所有本 session 卷任务并退出 |
| 10 Status | worker→GUI | job_id,state,volumes[{serial,stage,processed,files,checkpoint,error}] |
| 11 Published | worker→GUI | serial,generation,commit_seq,published_next_usn；GUI 仍须独立校验磁盘文件 |
| 12 Error | worker→GUI | code,retryable,volume?,operation,win32_code?,detail；不能编码为“空结果成功” |
| 13 Stopped | worker→GUI | reason,last_durable_checkpoints；最后通知，之后关闭 pipe |

控制请求队列容量 16，满时 Start/Pause/Cancel 不静默丢弃，返回 Busy 或合并同一最终状态；Shutdown 优先。Status 使用每卷最新值槽，默认每卷最多 4 次/秒；Published 合并到每卷最新 commit_seq。任何慢读端不能堵住取消和租约检查。

无凭消息 Ack 推进 durable checkpoint 的做法；以磁盘提交及 Published/Status 的最后持久位置为准。重复 job_id/请求采用幂等响应，不能重复启动同一卷写者。

## 4. 状态与退出

| 状态 | 合法转换 |
|---|---|
| Starting | Authenticated / Failed / Stopping |
| Authenticated | Preparing（收到 Start）/ Stopping |
| Preparing | Building / CatchingUp / Failed / Stopping |
| Building | CatchingUp / Degraded / Stopping |
| CatchingUp | Watching / Degraded / Rebuilding / Stopping |
| Watching | Paused / Compacting / Rebuilding / Degraded / Stopping |
| Compacting | Watching / Degraded / Stopping；逻辑查询仍使用旧已提交视图 |
| Paused | CatchingUp（恢复后重新验日志连续性）/ Stopping |
| Degraded | CatchingUp / Rebuilding / Stopping；不可用的单卷不拖垮其他卷 |
| Stopping | Stopped；不再接收新 Build/Compact |

GUI 每 1 秒发 Heartbeat。worker 用单调时钟计算认证连接的最后接收时间，12 秒无心跳取消；父进程句柄 signaled 或 pipe EOF 立即取消，不等待 12 秒。暂停状态仍需心跳。取消向原始卷 IO 发 CancelIoEx，并在解析/循环边界检查；取消未完成的 staging 不发布。

停止：拒收新任务 → 取消读卷/预热/合并 → 完成或放弃当前小帧事务并确定真实提交状态 → flush 已完成日志 → 释放 writer 锁 → Stopped → 关闭句柄与进程。GUI 异步等待默认 5 秒，超时记具体任务和仍存活 PID；不把强杀当正常退出验收。验证进程可强杀自己启动的实例做故障测试，必须标为异常中断。

日志缺口重建与损坏文件恢复依据 STORAGE。一般卷错误默认 30 秒重试；永久不支持/权限拒绝不自动反复提权。WorkerBusy 不启动第二写者或偷删锁。

## 5. 必测安全与生命周期场景

伪造同名 pipe、错误 PID/创建时间/SID、未知版本、超长/半消息、重复请求、慢读端、断连、GUI 崩溃、UAC 拒绝、卷 IO 取消、第二写者、索引根 reparse、路径在验证后替换、文件只读/磁盘满。覆盖 worker 使用普通 token 写入的断言，并以管理员专有测试位置验证拒绝（仅使用隔离位置，不改系统文件）。

参考：[GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)、[ImpersonateNamedPipeClient](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient)。实施前核实 API 与错误语义。
