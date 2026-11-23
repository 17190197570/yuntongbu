# 第七阶段：运行健康中心开发交付

日期：2026-11-20。工作源码版本为 **0.7.0**，SQLite schema **5**。本次在已有工程中增量加入健康中心，复用当前用户 Bootstrap → Agent → CloudProviderHost、管理管道、日志、缓存和归档执行器。`artifacts/Release` 中封存的第六阶段 0.6.0 MSI/源码包保持独立，本次源码包位于 `artifacts/Phase7`。

本阶段是可构建、可测试的开发实现，不代表七个阶段全部验收。第三阶段仍只有新增上传试验，已有文件修改上传、完整双向同步未完成，实际新增文件转换曾失败 `0x8007018B`；健康中心会持续提示 `FeatureIncomplete`，不会把此事实包装为正常双向编辑。用户已确认没有真实 NAS、可信签名证书及 HTTPS 更新站点，相关验收继续保留。

## 实现位置与数据流

| 源码 | 职责 |
|---|---|
| `src/CloudDrive.Storage/HealthModels.cs` | 六种状态、模块配置、过期规则、停滞阈值、检查/异常/修复模型 |
| `HealthMonitor.cs` | 独立后台调度、超时、防重入、修复白名单及复检 |
| `HealthProbes.cs` | Windows 进程/IPC、CFAPI、SMB、队列、缓存、归档及 SQLite 检查 |
| `HealthStore.cs` | 完整模块批次、故障合并、历史、配置、修复日志及保留策略 |
| `HealthDiagnosisEngine.cs` | 确定性依赖规则和单文件只读诊断 |
| `HealthReports.cs` | 逐字段脱敏预览、JSON/HTML/ZIP 导出、通知去重 |
| `Migrations/005_health.sql` | 四张健康表及索引，增量迁移至 v5 |
| `src/CloudDrive.Agent/ProductDaemon.cs` | 管理接口、旧生命周期/执行器适配、退出取消 |
| `src/CloudDrive.Desktop/MainForm.Health.cs` | 第九页运行健康中心、风险确认、设置、通知和导出 |
| `tests/HealthTests/Program.cs` | 52 项真实 Windows/SQLite/显式故障注入检查 |
| `tests/ProductTests/Program.cs` | 备份兼容、真实 IPC、可选空 Agent 生命周期检查 |

```text
Bootstrap 监视 Agent 退出，沿用原退避预算
    Agent 业务调度与文件工作线程
        ├─ 当前用户管理管道 → Desktop/托盘
        ├─ 原 SMB/CFAPI/同步/缓存/归档执行器
        └─ 独立 HealthMonitor Task
              → 串行、有预算的各模块探测
              → SQLite 完整模块批次 + 异常合并
              → 缓存快照 + 确定性诊断
              → 具体修复计划 → 风险确认 → 原执行器
              → 等待独立正向检测 → Verified
```

健康检查不进入 FETCH_DATA 数据传输路径，不重新注册根，不释放/固定文件，不创建或移除 SMB 映射，不读取凭据，不读取本地占位正文。SMB 探测最多读取远端根目录一个普通文件的一个字节；单文件诊断仅打开远端版本验证句柄。发现远端变化时仍由既有数据桥拒绝旧版本，健康中心不修改版本映射。

## 检查依据与边界

| 模块 | 实际检测依据 | 范围限制 |
|---|---|---|
| Process | 真实 PID、业务调度更新时间、对当前用户 Agent 的实际状态 IPC 往返 | Agent 不存在时由独立 Bootstrap 负责；UI 在 IPC 失败时显示 Unknown；没有以 UI 关闭判故障 |
| CFAPI | `CfGetSyncRootInfoByPath` 根身份、受监控 Host PID、READY、独立 CFAPI 心跳，15 分钟内最多 500 条回调日志 | 不以注册成功等同连接成功；外部/UAC Agent 未被后台观察时 Unknown；尚无每次请求时延和重复根全局枚举 |
| SMB | 每连接 DNS、TCP、当前会话拥有记录、实际 `Get-SmbMapping` 身份匹配、目录列表、可选一字节只读读取 | 不重新认证保存的密码，不做写权限试写；空根未验证文件读取；执行策略限制保留；未建立会话为 Unknown |
| Sync | 最旧最多 1001 条任务、持续失败/冲突、多次观察的无进度 Processing、大小/速度相关阈值 | 当前试验执行器不是完整双向同步；尚无独立 watcher 心跳、所有 Pending/事件一致性和重复执行专项诊断 |
| Cache | 最旧 16 项实际 CFAPI 分配/固定元数据与统计对比、释放失败、长期 Processing、未完成同步关联的释放候选 | 抽样不等于全盘校验；尚无独立自动释放调度心跳 |
| Archive | 最多 100 组原事务阶段、目标提交待删源、失败/冲突、长期 Copying/Verifying；源/目标由 SMB 模块报告 | 活动事务不提供并行恢复；尚无独立调度心跳、目标剩余空间和全部映射一致性专项探测 |
| Database | 存在性、Windows 只读属性、版本、14 个必要表/索引对象、实际写锁并回滚、孤立连接映射 | 不是所有 schema 对象/语义不变量的完整审计；数据库大小列出实际字节，尚未实现增长趋势告警 |
| DatabaseFull | 实际 `PRAGMA integrity_check` | 每周或手动运行，独立超时；不在每次心跳执行完整性扫描 |

SMB 每连接子预算最多 3 秒，总预算服从配置；预算不足的连接逐个标记 Unknown。超时同步 Windows 调用可能继续收尾，同模块不会再启动第二个实际探测。模块执行前显示 Checking，完成后一次事务提交同一 BatchId。最新完整批次替换旧全局超时和旧连接集合；历史异常不因为连接从列表消失而假装恢复。

状态聚合依次为 Critical、Degraded、Warning、Unknown、Checking、Healthy。单个连接断开为 Degraded；未配置存储、空闲和暂停本身不是故障。完成时间超过 `max(60秒, 2×模块间隔)` 时变为 Unknown。Unknown 不解决已有异常。异常变换根因记为 Reclassified，只有正向 Healthy 检测才记录 Resolved。

默认间隔：Process 30 秒；CFAPI/Sync 60 秒；SMB/Cache/Archive 300 秒；Database 600 秒；DatabaseFull 604800 秒。其余模块每日额外检查，用户可请求完整检查。设置页面允许启停、10–604800 秒间隔、1–120 秒预算和警告/严重阈值。检查串行，自动校准单独最多一个任务，退出时取消并等待收尾。

## 诊断与安全修复

规则引擎只解释实际代码和关联依赖，依赖同时异常不等于已证明唯一根因。文件诊断依次核对本地属性、Provider、稳定 FileId、SQLite、归档路由、缓存保护、当前 SMB 映射和远端版本只读句柄；不按原 NAS 路径猜测归档后的文件位置。文件诊断最多一个实际在途任务，15 秒显示超时，原调用结束前不重入。

| 操作 | 风险与条件 | 执行方式 |
|---|---|---|
| RecalibrateCache | Low；自动默认关闭、可选启用，每故障自动最多一次 | 原缓存校准，仅元数据，不释放/下载/删除 |
| Reconnect | 用户确认；新鲜证据且普通 445 | 请求原当前用户连接工作器，不拆除其他映射，不自动 UAC |
| RestartHost | 用户确认；原生命周期仍拥有该连接 | 取消旧工作器并等待安全退出，再由既有工作器启动，不强杀、不注销根 |
| ResumeArchive | 先确认计划，再明确授权可能删除源 NAS 文件；原 run 必须为 RecoveryRequired/Failed | 原事务日志、独占执行锁、版本/哈希/映射保护仍有效；活动阶段不提供恢复 |

不存在万能修复接口。删除文件、覆盖数据库、重建根、批量改映射、清空任务和强制拆除会话不在健康修复白名单。数据库恢复仍使用第六阶段离线维护、备份和兼容性检查入口。请求确认字段不能授权白名单以外动作。

动作状态为 Running → AwaitingVerification → Verified；提交成功不解决异常。失败保留 Failed 和原异常；Agent 重启将遗留 Running 记为 Interrupted，不能伪造完成。SMB 自动退避/任务恢复继续使用原模块机制，本阶段没有新增重复上传重试器。健康数据库访问使用 ReadWrite 模式，数据库丢失时不自动创建空库替代。

## 页面、通知与报告

运行健康中心提供全局数量/快照、模块及依据、异常与修复、模块/等级/时间筛选、最近 100 次同资源检查、立即完整检查、具体修复、设置和单文件诊断。模块详情采用结构化字段和检测依据，尚未把所有最近成功/失败业务时间、运行时长及原始日志做成独立列。

严重故障主动通知，Warning/Degraded 默认关闭，恢复通知可配置。复用现有 NotifyIcon 的 Windows 托盘系统提示，最多每 30 秒一条；未恢复故障使用 IncidentId 去重。通知账本保留全部仍 Open 故障的标记，终态最多 1000 条/30 天；实际数据库 Open 集合避免 500 条显示窗口之外的同一故障重复通知。系统是否显示提示受 Windows 通知设置影响，真实通知可见性未人工验收；未增加独立 WinRT Toast/AUMID 通知客户端。

导出前显示逐字段脱敏的固定预览，支持 JSON、HTML、ZIP。ZIP 只含 `health.json` 与 `health.html`，不含数据库、原始日志、凭据、DPAPI 密钥或用户正文。HTML 编码文本，JSON 不做整串截断。报告含模块依赖、检查依据、7 天计数、最近 24 小时及仍未解决异常、最多 100 条修复和可选文件诊断。资源 ID 保留用于追溯。异常显示最多 500 条，优先未解决和 Critical；全量仍在数据库。

## 迁移与保留

v4→v5 先使用 SQLite 在线备份保存 `Backups/before-v4-*.db`，再在事务中创建 HealthCheckRecords、HealthIncidents、RecoveryActions、HealthConfiguration，不重建连接、文件、缓存或归档映射。v4 备份 ZIP 经原 hash/版本校验后仅迁移暂存副本，再走原离线恢复保护；未来版本拒绝降级。

每日清理检查历史：7 天/最近 20000 条，同时额外保留每模块最后一条（最多 8 条），避免高频模块挤掉每周完整性检查的依据。终态普通异常 30 天，Critical 365 天；未解决异常长期保留；终态修复 180 天。末次依据仍使用原时间进行过期判定，保留不等于永远正常。

## 使用与验证

从工程目录执行：

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\start-smb.ps1
# 打开“运行健康中心”，先看结果/依据，再请求完整检查。
.\scripts\test-health.ps1
.\scripts\test-product.ps1 -Lifecycle
.\scripts\test-all.ps1 -ValidationDirectory artifacts\validation\phase7-delivery
.\scripts\package-phase7-source.ps1
```

Lifecycle 专项只允许无连接/未完成任务、没有预先运行 Agent 的专用配置，测试才会注入空 Agent 退出并验证 Bootstrap 重启；不对实际文件工作器强杀。测试日志、真实 Windows 故障注入及未验收矩阵见 [PHASE7_VALIDATION.md](PHASE7_VALIDATION.md)。

后续优先补齐真实 NAS 验收、第三阶段转换及双向同步前置能力，再补独立 watcher/缓存/归档调度心跳、全队列一致性、数据库增长趋势和完整业务时序；这些是尚未实现的检查能力，不能记为“只差真实 NAS 测试”。
