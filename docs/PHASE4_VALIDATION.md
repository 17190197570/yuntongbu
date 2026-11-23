# 第四阶段验证报告

日期：2026-11-20。环境：真实 Windows 11（build 26200）、当前用户会话、PowerShell、.NET 10。本报告对应第四阶段缓存实现；完整双向同步尚未完成，不能把本报告作为第三阶段验收通过的证明。

## 结果与证据范围

第四阶段缓存策略、持久任务和真实 Windows CFAPI 测试通过。**真实 NAS 未验收；全量回归存在第三阶段失败/系统阻断，整体未全部通过。**

最终回归命令：

```powershell
.\scripts\test-all.ps1 -ValidationDirectory 'artifacts\validation\phase4-release'
```

最终退出码 **1**，原因是第三阶段上传测试被系统阻断。其余本轮检查通过，具体结果见下面的表和 [summary.txt](../artifacts/validation/phase4-release/summary.txt)。日志目录：[phase4-release](../artifacts/validation/phase4-release)。该目录与较早的 phase4、phase4-final、phase4-delivery 分开保存，没有覆盖历史失败。

| 检查 | 结果 | 证据与范围 |
|---|---|---|
| Release 构建 | 通过 | [build-release.log](../artifacts/validation/phase4-release/build-release.log)，0 警告、0 错误 |
| 命令/路径安全 | 通过，30 项 | [unit.log](../artifacts/validation/phase4-release/unit.log) |
| 第二阶段 | 通过，42 项 | [phase2.log](../artifacts/validation/phase4-release/phase2.log)，含 Credential Manager、SQLite、流式 IPC、错误分类；SmbShare 模块导入仍被执行策略阻止，测试验证了阻断分类，不代表 SMB 连接通过 |
| 第三阶段新增上传 | 系统阻断，未通过 | [phase3-new-upload.log](../artifacts/validation/phase4-release/phase3-new-upload.log)，详见下文历史实际失败 |
| 第四阶段缓存 | 通过，38 项策略/队列 + 19 项真实 CFAPI 断言 | [phase4-cache.log](../artifacts/validation/phase4-release/phase4-cache.log)，两类证据分开说明 |
| 本地 CFAPI | 通过，10 组 | [cfapi-local.log](../artifacts/validation/phase4-release/cfapi-local.log)，含缺失 modifier 的快速拒绝和旧根原地更新 |
| SQLite 映射 CFAPI | 通过 | [cfapi-indexed.log](../artifacts/validation/phase4-release/cfapi-indexed.log)，元数据占位、水合、只读及释放 |
| 桌面冒烟 | 通过，五页 | [desktop-smoke.txt](../artifacts/bin/desktop-smoke.txt)，截图见下文 |
| 三类 NAS 测试入口构建 | 通过 | nas-harness-build、nas-new-upload-harness-build、nas-cache-harness-build 日志；仅编译，没有真实 NAS 运行 |
| 脚本语法 | 通过 | [summary.txt](../artifacts/validation/phase4-release/summary.txt) 的 PASS script-syntax；无控制台输出，因此 Tee-Object 未生成该组独立日志 |

## 策略与异常验证

`tests/CacheTests/Program.cs` 先执行 38 项显式替代平台检查，再在 `--cfapi` 模式下执行 19 项真实 Windows CFAPI 检查。控制台的“38 checks”只汇总前一部分，不是全部检查总数；两部分共 57 个 Check 断言。

38 项策略/队列检查使用 `FixturePlatform` 注入分配量、可用磁盘空间和故障，覆盖：

- v2→v3 增量迁移保留文件及同步任务，自动清理默认关闭。
- LRU、保护期、未知访问保守排除，后台打开不刷新有效访问时间。
- 普通文件、未同步、修改范围、固定、打开、水合中、部分水合和异常状态的拒绝。
- 待上传及规划后新建的同步任务阻止释放，特殊目录名称和路径边界正确。
- 容量高低水位、同卷多根统一磁盘预算。
- 失败保留真实缓存状态；离线 WaitingRemote、恢复重试及任务去重。
- 暂停/恢复/取消、执行后崩溃日志核对、跨管理器执行锁、后台启动恢复。
- 文件/目录保护及子项显式取消固定，缓存行丢失后的校准恢复。
- 完整水合的实际状态校正丢失完成提示后残留的下载计数。

这些检查验证策略和日志行为，**没有模拟删除文件来冒充 CFAPI 释放，也不代表已验证真实断网、Windows 重启或真实磁盘耗尽。**

## 真实 CFAPI 检查

测试启动实际 C++ Host，使用 SMB 身份、SQLite 映射和 Agent 数据桥，但远端接口连接专用本地源目录，明确标为 **LOCAL SOURCE, NOT NAS**。没有连接 NAS，也没有测量 SMB 网络性能。

验证内容包括：

- 元数据占位不读正文，3 MiB + 123 字节文件水合后 SHA-256 一致。
- 查询真实水合字节和主数据流分配空间；外部 Python 进程打开产生实际 CFAPI 访问提示，不伪造用户 LRU。
- 只读占位文件固定、完整水合、固定时拒绝释放，取消固定单独核对状态。
- 活动读句柄下拒绝释放。
- `CfDehydratePlaceholder` 成功后占位、身份、路径、逻辑大小仍保留，水合内容字节为 0；再次读取实际重新水合，源文件内容不变。
- 删除专用测试数据库中的缓存行后，根据真实占位状态恢复统计；未删除本地占位或索引文件记录。
- 513 个小文件跨越 256 条写入批次及 512 条数据库分页；目录递归固定、取消固定及释放全部完成。
- 1 GiB + 123 字节文件按流读取、哈希校验、释放和再次水合，SHA-256 一致。源为稀疏/零填充测试文件，不能替代随机大文件或真实 NAS 性能验收。

大文件的内存指标是托管测试进程的 `PrivateMemorySize64` 在完整读取前后的差值，本轮为 **6,414,336 字节（约 6.12 MiB）**，**不是连续采样峰值，也不包括全部原生进程/系统文件缓存内存**。原始值见本轮 `phase4-cache.log`；测试上限为 128 MiB。

独立的 10 组本地 CFAPI 测试还检查：旧根缺失 AUTO_DEHYDRATION_ALLOWED 时直接释放快速拒绝；正常启动仅对精确拥有且来源身份一致的根原地更新策略，保留根 file ID、占位身份及未同步内容。Hydration 保持 FULL，Population 保持 ALWAYS_FULL。没有注销重建同步目录。

## 第三阶段与系统阻断

最终全量运行中，`Phase3Tests.dll` 被 Windows 应用控制以 `0x800711C7` 阻止加载，因此该组标为 FAILED/BLOCKED，不能判定通过。没有关闭或绕过系统策略。第二阶段的原生 SmbShare 模块也存在执行策略限制，这是部署验收的前提项。

此外，[早期实际运行日志](../artifacts/validation/phase4/phase3-new-upload.log) 已记录另一个真实问题：新增上传在提交远端文件后，独立 Agent 调用 `CfConvertToPlaceholder` 失败，HRESULT `0x8007018B`（CLOUD_FILE_ACCESS_DENIED）。本地替代 finalizer 的 23 项检查通过不能覆盖这个失败；最终系统阻断也不能推翻它。

当前默认占位管理策略下应由已连接的提供程序处理转换。没有为通过测试而开放 unrestricted 占位管理，没有重写第三阶段，也没有声称安全双向编辑已经完成。失败/提交中的任务与内容保留，缓存筛选会拒绝释放；后续应通过受控 Host 操作修复并单独验收。

## 未验收矩阵

| 项目 | 本轮可用证据 | 未完成的真实验收 |
|---|---|---|
| NAS 释放/再次打开 | 本地实际 CFAPI 释放及程序读取重新水合 | 真实 SMB/NAS、Shell 双击、云朵图标、远端原文件与路径不变 |
| NAS 断网/恢复 | 替代平台 WaitingRemote、重试去重 | 拔网、SMB 会话失效、NAS 重启及连接恢复 |
| 系统恢复 | 持久任务恢复和实际状态核对 | Agent 被终止、Windows 重启/重新登录、休眠唤醒的真实端到端测试 |
| 使用中与未上传保护 | 实际读句柄 + 替代平台同步任务/修改/冲突筛选 | Office 正在编辑、真实上传并发、完整双向冲突流程 |
| 多存储及磁盘压力 | 替代平台多根共享卷预算、跨实例锁 | 多 NAS、多真实卷、突发低磁盘、1–4 并发压力 |
| 性能及异常文件 | 513 小文件、1 GiB + 123 文件、实际分配查询 | 百万文件、随机超大文件、内存峰值曲线、更多 NTFS/重解析/损坏占位边界 |

用户已明确当前没有 NAS，本轮记录为未验收。没有为此保存测试密码或修改其他 SMB 映射。

## 界面、日志及复现

缓存页包含总览/文件、策略、清理历史和逐文件原因。无连接时明确显示无可读卷，自动清理默认关闭。截图来自最终构建的桌面冒烟：

- [缓存文件与总览](../artifacts/bin/cache-center-smoke.png)
- [清理策略](../artifacts/bin/cache-policy-smoke.png)
- [桌面冒烟结果](../artifacts/bin/desktop-smoke.txt)

最终缓存 fixture：`artifacts/tests/cache-d0e0e56503b347fe9059763e3d343e30`。从其 `index.db` 以 SQLite `mode=ro` 只读导出：

| 文件 | 内容 |
|---|---|
| [cache-evidence.json](../artifacts/validation/phase4-release/cache-evidence.json) | 原始数据库路径、证据范围、导出计数 |
| [cache-real-runs.json](../artifacts/validation/phase4-release/cache-real-runs.json) | 实际 CFAPI 的 9 次任务汇总 |
| [cache-real-operations.csv](../artifacts/validation/phase4-release/cache-real-operations.csv) | 实际 CFAPI 的 1,547 条逐项记录，明确 LOCAL SOURCE, NOT NAS |
| [real-provider.log](../artifacts/validation/phase4-release/real-provider.log) | 实际 Host 的水合、访问及 CFAPI 输出 |
| [cache-fixture-runs.json](../artifacts/validation/phase4-release/cache-fixture-runs.json) / [cache-fixture-operations.csv](../artifacts/validation/phase4-release/cache-fixture-operations.csv) | 显式替代平台的任务和逐项记录 |
| [task-execution.log](../artifacts/validation/phase4-release/task-execution.log) | 替代平台检查阶段的日志摘录；完整操作记录在上面导出文件中 |

实际 CFAPI 操作记录中 1,545 条 Completed、1 条 Skipped（固定保护）、1 条 Failed（测试故意持有读句柄，原生 oplock 返回 `0x80070020`）。后两项是安全拒绝测试的预期结果；不应把这个预期 Failed 记录误报为清理功能回归失败。成功释放的主数据流实际分配合计 **1,078,996,992 字节**，包括大文件、513 小文件及单文件释放。固定/取消固定本身不记作释放。测试源、数据库及非空测试根保留，没有为了清理测试痕迹删除用户文件或强制注销非空根。本轮结束检查未发现残留 CloudProviderHost 进程。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test-cache.ps1                # 仅明确的替代平台策略/队列检查
.\scripts\test-cache.ps1 -Cfapi          # 增加实际 Windows CFAPI、本地源
.\scripts\test-integration.ps1          # 10 组本地 CFAPI
.\scripts\test-indexed-cfapi.ps1
.\scripts\test-all.ps1 -ValidationDirectory 'artifacts\validation\phase4-release-recheck'

# 有真实 NAS 后，仅对专用测试连接/已存在文件执行；密码通过现有安全入口保存：
.\scripts\test-cache-nas.ps1 -ConnectionId '<GUID>' -RelativePath '缓存验收.bin'
```

NAS harness 只构建未运行；它验证水合、固定、取消固定、释放、再次水合及双方哈希，不创建/删除远端文件。运行前需要结束该连接的 Agent，且无待同步/缓存任务和既有保护意图；期间禁用该连接自动策略，结束恢复。

源码、增量迁移和执行链见 [PHASE4.md](PHASE4.md)。优先后续工作是解决第三阶段受控占位转换，再在专用真实 NAS 环境验收上表项目。归档、实时远端监控及完整双向同步不属于本轮缓存交付。

## 保留的中间失败

- 首次回归 `artifacts/validation/phase4` 保留第三阶段实际转换失败和桌面应用控制阻断。
- 中间回归 `artifacts/validation/phase4-final` 的目录释放有一个文件因丢失完成提示导致残留下载计数而跳过，已增加真实完整水合校正并复测；旧根迁移测试也修正了“用户改写后可能已变为普通文件”的断言，保留其内容与状态而不强行要求占位。
- `phase4-delivery` 的 38 + 19 缓存检查已通过，但不包含最后增加的缺失 modifier 快速拒绝断言；最终 `phase4-release` 包含该修补及全量复测。
