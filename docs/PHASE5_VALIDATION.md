# 第五阶段验证报告

**第六阶段追加记录（2026-11-20）：** [phase6-final归档回归](../artifacts/validation/phase6-final/phase5-archive.log)已实际通过56+10项，补齐此前物理目录重叠保护的运行验证，1GiB+123前后内存差3,227,648字节。随后最终交付回归的56项本地检查仍通过，但新构建Host被应用控制阻止，见[最新完整报告](PHASE6_VALIDATION.md)。下文保留第五阶段当时的历史结果，不用旧成功替代最新阻断，也不把本地目录当NAS。

日期：2026-11-20。环境为真实 Windows 11 build 26200、当前用户会话、.NET 10、PowerShell。用户当前没有 NAS，未保存或虚构双 NAS 测试结果。

**归档读取/迁移链已实现并完成本地验证；第五阶段整体尚未验收。归档后编辑回传尚未实现，第三阶段完整双向同步不是已完成前提。**

## 执行入口与结果

```powershell
.\scripts\test-all.ps1 -ValidationDirectory 'artifacts\validation\phase5-release'
```

最终全量运行**退出码为 1**，以 [summary.txt](../artifacts/validation/phase5-release/summary.txt) 和下列原始日志为准。第三阶段实际占位转换失败；归档、索引 CFAPI、桌面三组被 Windows 应用控制阻止加载 DLL（`0x800711C7`），没有运行到断言。没有关闭、修改或绕过系统策略。

| 组 | 最终状态 | 证据 |
|---|---|---|
| Release 构建 | 通过，0 警告/0 错误 | [build-release.log](../artifacts/validation/phase5-release/build-release.log) |
| 30 项命令/路径检查 | 通过 | [unit.log](../artifacts/validation/phase5-release/unit.log) |
| 42 项第二阶段检查 | 通过 | [phase2.log](../artifacts/validation/phase5-release/phase2.log)；本地数据桥及安全凭据检查，不等于真实 SMB 连接通过 |
| 第三阶段新增上传 | 实际转换失败 | [phase3-new-upload.log](../artifacts/validation/phase5-release/phase3-new-upload.log) |
| 38 + 19 项第四阶段缓存断言 | 通过 | [phase4-cache.log](../artifacts/validation/phase5-release/phase4-cache.log) |
| 第五阶段归档 | 系统阻止 DLL 加载 | [phase5-archive.log](../artifacts/validation/phase5-release/phase5-archive.log)；此前通过的 56 + 10 项证据见下文 |
| 10 组原生 CFAPI 回归 | 通过 | [cfapi-local.log](../artifacts/validation/phase5-release/cfapi-local.log) |
| SQLite 映射 CFAPI 回归 | 系统阻止 DLL 加载 | [cfapi-indexed.log](../artifacts/validation/phase5-release/cfapi-indexed.log) |
| 六页桌面冒烟及配置编辑器 | 系统阻止 DLL 加载 | [desktop-smoke.log](../artifacts/validation/phase5-release/desktop-smoke.log) |
| 四类 NAS harness | 仅构建通过，未连接 NAS | [基础](../artifacts/validation/phase5-release/nas-harness-build.log)、[新增上传](../artifacts/validation/phase5-release/nas-new-upload-harness-build.log)、[缓存](../artifacts/validation/phase5-release/nas-cache-harness-build.log)、[归档](../artifacts/validation/phase5-release/nas-archive-harness-build.log) |
| PowerShell/Python 脚本语法 | 通过 | summary.txt 中的 script-syntax 结果；无控制台输出时没有独立 Tee 日志 |

随后使用原脚本入口有限重试：[归档](../artifacts/validation/phase5-release/archive-retry.log)、[索引 CFAPI](../artifacts/validation/phase5-release/indexed-retry.log)、[桌面](../artifacts/validation/phase5-release/desktop-retry.log)仍被阻止；最后一次[归档专项重试](../artifacts/validation/phase5-release/archive-final-retry.log)也退出 1。没有更换加载方式或修改策略。

## 证据分类

最近一次成功的专项命令是 `.\scripts\test-archive.ps1 -Cfapi`，退出 0，保留 fixture：`artifacts/tests/archive-e8fae47d31de4e67b3062f396a6a7db3`。本节通过结果来自该次运行，**不是最终全量日志**。该次完整控制台输出未另存为日志；保留的数据库、逐文件导出和原生 Host 日志见文末。

该成功运行早于最后新增的 `ArchiveFile.CheckPhysicalOverlap` 目录/祖先物理身份重叠保护。最后这项改动已随最终 Release 构建通过并完成代码检查，**运行验证待补齐**，不能将此前 66 项通过声称为最终全部源码的运行结果。

56 项归档策略/文件 I/O 断言：实际在 Windows 文件系统上创建可丢弃的专用文件，使用真实句柄、文件 ID、无覆盖重命名、时间属性、SHA-256、SQLite 事务及持久日志；本地缓存状态与 CFAPI 独占保护通过明确命名的 `ExplicitFixtureCache` / `ExplicitFixtureGuard` 替代。它们不能证明 CFAPI 保护或真实 SMB 协议。

另 10 项真实 CFAPI 断言：启动实际 C++ Host，注册实际同步根、创建占位、使用实际 WindowsArchiveLocalGuard（CFAPI oplock + protected reference）和 WindowsCachePlatform，通过现有命名管道及映射读取接口。源、目标是**两个本地目录，不是 NAS**。源测试文件删除是归档流程的一部分，本地占位未删除，缓存释放调用实际 CFAPI。

控制台先打印 56 项摘要，再打印实际 CFAPI 的 10 个 Check 结果，总计 **66 个第五阶段断言**，不是 66 个真实 NAS 测试。

## 时间、扫描和配置

验证默认关闭、180 天、周日 02:00、各配置相互独立；严格测试创建/修改均早于阈值、任一较新、精确等于阈值和不可靠时间。使用专用测试文件设置旧时间，没有修改业务文件的时间。

检查只预览没有创建目标目录或写入文件；默认隐藏属性过滤可单独启用，非递归只包含直接子文件，中文/空格/`# % &` 以及嵌套目录正常；路径穿越拒绝。实际超过 260 字符的 Unicode 路径迁移通过，SMB 和 Shell 长路径仍未验收。

v3→v4 使用实际数据库备份、移除本阶段表模拟 v3 后重新增量迁移，保留连接、逻辑文件和缓存策略。另有旧同步/缓存迁移回归。连接被归档引用时不能重定向来源或删除目标连接。

## 完整性与恢复

已覆盖以下本地检查：

- 实际源文件和目标临时文件分别流式计算 SHA-256，重新打开正式目标和新映射再次回读；精确创建/修改时间读回一致才继续。
- 目标既有同名、提交时另一文件抢占名称、未知临时文件均不覆盖；明确选择唯一冲突名称时保留原同名目标。
- 实际 SQLite 触发器在映射 INSERT 时抛出事务错误：已提交目标和源均保留，映射未切换，移除测试触发器后恢复成功。
- 在目标提交、映射完成/删除意图处注入异常：检查源、目标、映射和日志后恢复，不重新上传/盲删目标。
- 实际 Windows 只读源文件导致按句柄删除失败：新映射及两份内容保留，不计删源；仅修正测试源只读属性后重试成功。
- 目标临时文件被注入错误内容或时间：校验拒绝提交，源保留。
- 复制完成后暂停并改写源：恢复识别版本变化，只删除能够证明归属的无效临时副本，保留当前源和冲突日志。
- 实际另一个源读句柄导致共享模式拒绝；本地未同步/用户保护/固定排除、跨配置同文件预留、全局跨实例执行锁、暂停/恢复及未开始取消。
- 归档完成后重新扫描主存储不丢失原逻辑条目；主存储中空父目录被测试删除后，其被归档文件所需的逻辑父目录仍保留。
- 257 个小文件跨越 256 条归档日志分页，四路并发完成，各文件独立校验并确认删源。
- 周/月计划、本地时区执行窗口和持久 DueKey：错过的周期合并补一次；全局暂停在重建管理器后仍有效。

异常注入和重建管理器不是操作系统断电测试。未执行真实进程强杀、Windows 重新启动、NAS 断网或数据包级故障。

## 实际 CFAPI 结果

专用根内准备一个 3 MiB + 123 字节仅联机文件、一个同大小已完整水合且固定的文件，以及一个 **1 GiB + 123 字节**测试文件。大文件用 SetLength 创建零填充文件，不代表随机大文件网络性能。

执行后验证所有目标哈希和时间，原源文件通过准确句柄删除，FileId/原本地路径保持；固定文件的固定状态、水合字节、真实分配空间保持，原仅联机文件和大文件归档时没有本地下载。

通过原占位路径真实读取，从新映射目标水合并校验；原主存储扫描保留命名空间。独立缓存释放按新映射完成 CFAPI 脱水，目标原文件保留，再次读取重新水合。大文件也通过原 CFAPI 路径读取并得到与归档日志相同的流式 SHA-256。

内存检查采用测试托管进程完整归档前后的 PrivateMemorySize64 差值，上限 128 MiB；最近成功专项的差值为 **3,592,192 字节**。**不是连续峰值采样，也不包括原生 Host、Windows 文件缓存或网络栈**。该值来自成功专项的控制台观察，最终 phase5-archive.log 只有策略阻断信息，不包含此项运行数据。

界面曾在首次全量回归通过六页与归档编辑器冒烟；[冒烟记录](../artifacts/validation/phase5-release/desktop-smoke.txt)、[归档执行中心截图](../artifacts/validation/phase5-release/archive-center-smoke.png)、[归档配置截图](../artifacts/validation/phase5-release/archive-policy-smoke.png)是该次成功的留存快照。最终桌面执行被系统阻止，不能用旧截图代替最新执行结果。

## 第三阶段实际失败

本轮第三阶段 23 项替代 finalizer 检查通过后，真实 `CfConvertToPlaceholder` 在 `CfNewFileFinalizer.cs:82` 返回 `0x8007018B`（CLOUD_FILE_ACCESS_DENIED）。不是本轮应用控制未运行的结果，也不是新增归档测试失败。独立 Agent 的实际转换与默认占位管理策略之间仍有问题，没有放宽 unrestricted 权限或改写原生 Host 来掩盖它。

这项失败及完整已有文件修改同步的缺失，阻止任务书中的“归档后编辑并同步回归档 NAS”验收。本轮保留只读。`ResolveStorage` 提供目标接口，新增上传遇到已归档文件拒绝写回原 NAS，但这只是安全接口准备，不能等同于已支持编辑上传。

## 未验收与限制

| 项目 | 当前证据 | 待完成 |
|---|---|---|
| 跨 NAS、不同账号/端口 | 相同生产 Mapper 接口、不同物理本地目录 | 真实双 NAS、认证冲突、445/自定义端口与受控提权 |
| 源/目标 NAS 断网或会话过期 | 持久失败/恢复逻辑及错误注入 | 两端断网、SMB 会话过期、NAS 重启 |
| 目标不足或不支持元数据 | 可用空间预检、写入失败保持源、时间不一致测试 | 真正 NAS 磁盘耗尽和不支持设置创建时间的设备 |
| CFAPI 访问体验 | 实际程序通过原路径重新水合 | Explorer 双击/云朵图标、Office 打开 |
| 归档后编辑回传 | 映射解析接口、禁止误传原 NAS | 已有文件修改写回、Host 更新、并发版本及冲突处理，尚未实现 |
| 重启和任务中断 | 日志重建、持久状态和管理器恢复 | 真实强杀、Windows 关机/登录、休眠唤醒 |
| 性能/并发 | 257 小文件、四路文件 I/O、1 GiB+123、实际 CFAPI | 真正多 NAS、多端改写、随机大文件、连续内存峰值、百万文件 |
| 路径/身份 | 本地长路径、安全组件、逻辑 ID 保持 | SMB 长路径、大小写敏感共享、别名/克隆卷 ID 的设备兼容性 |

同目录临时文件可能因未完成、未知归属或冲突而保留；没有自动删除不明副本的维护功能。目录属性/ACL/ADS/硬链接等完整元数据复制不在本轮验收范围。取消不会强杀正在进行的同步 Windows 文件系统调用。归档/删源容量是已确认文件的逻辑字节，不代表快照/去重 NAS 的实际物理可用空间增量。

## 交付与复现

源码、迁移、界面、数据流和 CLI：[PHASE5.md](PHASE5.md)。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test-archive.ps1
.\scripts\test-archive.ps1 -Cfapi
.\scripts\test-all.ps1 -ValidationDirectory 'artifacts\validation\phase5-recheck'
```

真实双 NAS harness 已提供，只构建未执行；要求专用旧测试文件、源 Agent 运行、关闭冲突的自动任务，并显式使用 `-AllowSourceDelete`。详细调用和源删除行为见 PHASE5.md。不需要在聊天或命令行传入密码。

已从最近成功专项的两个 SQLite 数据库以只读方式导出，来源及范围见 [证据清单](../artifacts/validation/phase5-release/archive-evidence-manifest.json)：

- 文件 I/O 层：[逐文件完整性 CSV](../artifacts/validation/phase5-release/archive-filesystem-integrity.csv)、[任务/映射/事务日志 JSON](../artifacts/validation/phase5-release/archive-filesystem-evidence.json)。274 条项目中 267 条完成，5 条 Conflict、1 条 Cancelled、1 条 Eligible 来自故障/预留测试；267 条完成记录哈希均一致，确认删源逻辑字节 329,913，267 条映射、2,981 条事务日志。
- 实际 CFAPI 层：[逐文件完整性 CSV](../artifacts/validation/phase5-release/archive-cfapi-integrity.csv)、[任务/映射/事务日志 JSON](../artifacts/validation/phase5-release/archive-cfapi-evidence.json)、[原生 Host 日志](../artifacts/validation/phase5-release/archive-cfapi-provider.log)。3 条全部完成、哈希一致并确认删源，逻辑字节 1,080,033,649，3 条映射、97 条事务日志；两端仍为本地目录。

旧的初次全量回归 `artifacts/validation/phase5-initial` 与失败 fixture 均保留。目录保护 SQL 转义（改用 `char(92)`）和长路径提交问题已修正，并包含在最近成功的 66 项专项中；只有随后新增的物理目录身份重叠保护尚缺运行验证。下一步先修复第三阶段受控 Host 转换及已有文件写回，再完成真实双 NAS 验收。
