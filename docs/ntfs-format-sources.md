# 磁盘解析依据与当前边界

Windows 平台项目已接入只读原始卷、MFT 初建、缓存视图核对、跨记录属性合并、紧凑索引保存及 USN 更新。自动验证使用构造的记录、模拟卷和损坏数据，不等于真实卷验收。

格式依据为 Microsoft 的 [FILE_RECORD_SEGMENT_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header)、[MULTI_SECTOR_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/multi-sector-header)、[ATTRIBUTE_RECORD_HEADER](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-record-header)、[USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2) 和 [USN_RECORD_V3](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v3)，另与研究中观察到的 512 字节更新序列步长交叉核对。

解析保留完整文件身份、父目录身份及原始 UTF-16 名称。先验证全部更新序列，再恢复私有副本；不完整记录和不支持版本会报错，不允许跳过后将日志位置标为已完成。

卷接口依据 [NTFS_VOLUME_DATA_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer)、[FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record) 和 [ATTRIBUTE_LIST_ENTRY](https://learn.microsoft.com/en-us/windows/win32/devnotes/attribute-list-entry)。文件记录接口可能返回请求编号之前最近的已用记录，必须核对实际编号及序列号；跨记录引用还要核对基段身份和属性实例。

初建前保存日志位置，通过 MFT 非常驻区段顺序读取，随后用文件系统枚举视图纠正原始盘面与缓存的差异，并追齐初建期间的变化。增量批次重新读取受影响文件的全部名称，因此重命名、移动和硬链接修改不依赖猜测单条事件的最终含义。日志身份改变、覆盖断档、读取不推进或父目录关系不完整都会失败，不能发布为有效索引。

当前每个索引只对应一个盘符 NTFS 卷，查询入口可合并多个卷；不包含远程文件系统、未挂载卷、其他格式卷和文件内容检索。名称与父目录身份保存，路径在最终展示时组装；原始名称包含隐藏项、系统项和 DOS 别名。快捷方式去掉 `.lnk` 后参与匹配，启动其原路径；启动动作去重和 UWP 来源仍待接入。外部属性列表不完整时明确失败。

真实卷已验证 C、D 两盘初建及 USN 同步。普通进程只读打开卷返回 Windows 错误 5，管理员只读进程成功；不创建日志、不修改 Listary、不安装服务。原始卷记录严格验证及恢复 USA；控制接口返回的逻辑记录不能再次套用旧 USA。本机 C 盘记录 380784 曾返回扇区尾 `0231`、USA 恢复值 `0000`，再次恢复会破坏缓存中的有效属性内容，现已分开处理并补充回归检查。

日志输入按 [READ_USN_JOURNAL_DATA_V0](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0) 的 40 字节布局，日志 ID 位于偏移 32。打开目标前通过 [FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info) 比较卷和文件身份，目录联接查询链接本身。真实覆盖与当前限制见 `index-validation-2026-11-13.md`；两盘当前记录数不等于已证明不存在其他遗漏。
