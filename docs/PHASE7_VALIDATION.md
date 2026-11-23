# 第七阶段验证与未验收记录

日期：2026-11-20；Windows 11 build 26200 x64，当前用户，.NET 10。源码 0.7.0，SQLite v5。无真实 NAS；无可信签名证书和 HTTPS 更新端点，按用户已有确认交付开发源码与测试证据。

## 最新完整回归

执行 `scripts/test-all.ps1 -ValidationDirectory artifacts/validation/phase7-delivery`，**整体退出 1**。不能描述为全部通过。[原始汇总](../artifacts/validation/phase7-delivery/summary.txt)。

| 项目 | 最新结果 | 原始证据 |
|---|---|---|
| 原生/Agent/Desktop/Bootstrap Release 构建 | 通过，零警告/错误 | [build-release.log](../artifacts/validation/phase7-delivery/build-release.log) |
| 命令/路径基础检查 | 30 项通过 | [unit.log](../artifacts/validation/phase7-delivery/unit.log) |
| 第二阶段 | **入口被 Windows 应用控制阻止**，本次未执行 | [phase2.log](../artifacts/validation/phase7-delivery/phase2.log) |
| 第三阶段新增上传 | 23 项替代检查通过；**实际 CfConvertToPlaceholder 失败 0x8007018B** | [phase3-new-upload.log](../artifacts/validation/phase7-delivery/phase3-new-upload.log) |
| 缓存 | 38 项策略/队列检查 + 19 项真实本地 CFAPI 检查通过 | [phase4-cache.log](../artifacts/validation/phase7-delivery/phase4-cache.log) |
| 归档 | 56 项本地事务/策略 + 10 项真实本地 CFAPI 检查通过 | [phase5-archive.log](../artifacts/validation/phase7-delivery/phase5-archive.log) |
| 产品常规检查 | 30 项通过，包含有效 v4 ZIP→v5 恢复 | [phase6-product.log](../artifacts/validation/phase7-delivery/phase6-product.log) |
| 健康中心 | **52 项通过**，真实 Windows/SQLite/回环 TCP + 明确标注的探测/执行器故障注入 | [phase7-health.log](../artifacts/validation/phase7-delivery/phase7-health.log) |
| 原生 CFAPI | 10 项通过 | [cfapi-local.log](../artifacts/validation/phase7-delivery/cfapi-local.log) |
| 索引/管道/CFAPI | 真实占位、水合、只读写入拒绝、释放通过 | [cfapi-indexed.log](../artifacts/validation/phase7-delivery/cfapi-indexed.log) |
| 桌面 | 九页渲染、真实 Close 隐藏和 Show 重开通过；健康截图已视觉检查 | [desktop-smoke.log](../artifacts/validation/phase7-delivery/desktop-smoke.log)、[截图](../artifacts/Phase7/health-center-smoke.png) |
| NAS/新增上传/缓存/归档四类 harness | 仅构建通过，无 NAS 运行 | 同目录 nas-*-harness-build.log |
| PowerShell / Python 语法 | 通过 | [script-syntax.log](../artifacts/validation/phase7-delivery/script-syntax.log) |

最后专项 `scripts/test-product.ps1 -Lifecycle`：**38 项通过**，在常规 30 项基础上实际启动空配置 Bootstrap/Agent，检查健康 IPC/业务调度/数据库、完整检查排队、拒绝危险修复、导出真实空 Agent 健康快照、重复启动抑制、注入空 Agent 退出、独立重启及安全关闭。[原始专项日志](../artifacts/validation/phase7-delivery/product-health-lifecycle.log)。没有 NAS 或文件任务参与进程退出注入，测试结束未留下 Agent/Host/Bootstrap 工作进程。

缓存 1 GiB+123 字节用例中托管内存前后差值 8,224,768 字节；归档同尺寸用例进程前后差值 4,837,376 字节。这是两种测量的前后差值，**不是峰值内存或长期稳定性证明**。两项均使用本地目录，不能替代真实大文件 SMB 验收。

## 第七阶段故障注入与恢复矩阵

| 场景 | 验证方式与结果 | 边界 |
|---|---|---|
| Agent 退出 | 真实空 Agent 退出后 Bootstrap 启动新 PID，管理 IPC/DB 恢复；通过 | 没有强杀文件操作或配置中的 Agent；带业务退出未验收 |
| Host 退出/未连接 | 已实现 PID、READY、独立心跳和根身份规则 | 第七阶段未执行受控活 Host 故障注入，不能标为通过 |
| SMB 网络/端口 | 实际 Windows 回环 DNS/TCP 成功后检查无会话为 Unknown，关闭监听后识别 PortClosed；通过 | 不是 SMB 认证、真实 NAS 断网或恢复 |
| SMB 认证失败/凭据冲突 | 使用既有会话的访问错误分类及配置建议已接入 | 无 NAS，真实认证失败未验收；不主动重新使用密码认证 |
| Sync Root 异常 | 实际 CFAPI 查询不存在测试根，返回 RootInvalid 且未创建/注册；通过 | 没有重建根 |
| 同步卡死 | 真实 SQLite Processing 任务，注入观察时钟，无进度变为 ProgressStalled，更新进度后清除疑似停滞；通过 | 这是确定性时钟注入，不是实际等候 30 分钟；没有完整双向同步 |
| 缓存释放失败 | 第四阶段失败队列、保留本地状态回归通过；第七阶段有只读规则 | 未单独注入真实健康中心的活释放失败；NAS 离线释放未验收 |
| 归档中断阶段 | 真实 SQLite journal TargetCommitted/Processing 不提供 Resume；RecoveryRequired 仍识别目标已提交待完成；通过 | 健康恢复引擎执行器使用显式注入；原归档引擎另有本地恢复回归 |
| DB 不可写 | 实际 Windows ReadOnly 属性；返回异常且未清除属性；通过 | 实际 ACL/磁盘只读/磁盘满未验收 |
| DB 损坏/缺失索引 | 实际非 SQLite 文件和 DROP INDEX 测试副本，返回明确异常且不修复；通过 | 检查范围为 14 个必要对象，不是完整语义审计 |
| DB 消失 | 初始化测试库后删除测试副本，健康存储拒绝 ReadWriteCreate 重建；通过 | 未操作用户数据库 |
| DB 锁冲突 | 实际 SQLite writer lock，结果 Unknown，未误报损坏；通过 | 不强制解锁 |
| IPC | 实际当前用户命名管道；活 Agent 健康状态与后台调度/数据库有正向证据；通过 | 生产 UI 不可达显示 Unknown 的分支已实现，未在活文件业务下断管道 |
| 检查超时 | 实际异步阻塞探测，Checking→Unknown，下轮不产生第二个在途探测；通过 | 同步系统调用可继续收尾，不强杀 |
| 修复成功 | 注入原执行器成功，仅 AwaitingVerification；独立 Healthy 检查后 Verified；通过 | 不等于真实 NAS 重连成功 |
| 修复失败 | 注入执行器抛错，Failed 记录及原 Open 异常保留；通过 | 不清理原任务 |
| 用户取消/未确认 | 直接 API 未提供风险授权，执行次数不变；活 Agent 拒绝确认字段也无法授权的 DeleteFiles；通过 | MessageBox 人工点击取消未单独验收 |
| 归档删源授权 | 无单独 DataAuthorized 拒绝 Resume，执行次数不变；通过 | 真实双 NAS 的删源授权/恢复未验收 |
| 重复故障/恢复 | SQLite 合并计数、Unknown 不解决、正向证据写恢复时间、复发新 IncidentId、通知去重；通过 | OS 系统提示可见性未人工验收 |
| 旧批次与删除连接 | 新模块批次替换旧全局超时；新超时压制旧绿色；消失连接不自动解决原故障；通过 | 不删除本地占位或业务索引 |
| 自动校准阻塞 | 明确启用的注入执行器阻塞时检查仍完成、最多一个恢复任务；成功仍等待复检；通过 | 默认关闭；不模拟实际 NAS 校准速度 |
| 历史保留 | 终态清理、保留最后模块依据/Open 异常、通知终态上限及窗口外 Open 去重；通过 | 未解决故障长期保留，不受终态历史上限删除 |
| 报告安全 | >4096 字符 JSON 仍合法，字段脱敏、HTML 编码、ZIP 精确两项；实际空 Agent 也完成导出；通过 | 未导出原始日志、DB 或正文 |
| v4→v5 迁移与备份 | 真实 SQLite 在线迁移备份保留配置；有效 v4 ZIP 原 hash 验证后迁移恢复，原 ZIP 不变；通过 | 正式 MSI 升级尚未验收 |

## 证据时序与应用控制

较早完整尝试 [phase7-final/summary.txt](../artifacts/validation/phase7-final/summary.txt) 中，多项程序集/EXE 和 Desktop 被应用控制阻止。补充数据库不重建、退出取消、历史保留等实际源码变更后，完成安全构建、52 项专项、34 项生命周期和九页 UI 检查；随后执行上表最新全量和新增健康 IPC 的 38 项生命周期专项。旧阻断日志保留，不能用较早通过掩盖最新失败，也不能把较早阻断描述成最新所有入口均不可运行。

最新仍被阻止的入口是 Phase2Tests.exe。相关 CodeIntegrity 3033/3077 事件保存在[最新事件记录](../artifacts/validation/phase7-delivery/code-integrity.json)，保留策略观察窗口内其他历史阻断。程序没有修改 Windows 安全策略、执行策略、文件名或程序集加载位置来绕过控制。第三阶段 `0x8007018B` 是实际功能失败，与应用控制阻断分开记录。

## 未实现与未验收

尚未实现：独立 watcher/缓存/归档调度心跳；完整队列 Pending/事件/状态/重复任务一致性审计；数据库增长趋势告警；全局重复根枚举与每个回调时延；全部模块业务时序的独立 UI 列；独立 WinRT Toast 客户端。当前规则、抽样和原日志查询不能代替这些能力。完整双向编辑同样未实现，必须继续保持既有文件只读保护。

未验收：真实 NAS 的认证/权限/会话冲突、断网/重启/恢复、多连接和自定义端口/UAC、写权限测试、大文件/海量目录、真实双 NAS 归档恢复；带任务的进程崩溃/休眠/网络切换/Windows 重登；OS 通知可见性和用户逐项交互；正式签名、更新站点、干净 VM 安装/修复/升级/卸载、非空根卸载 A/B、长期负载/多用户。原第六阶段 MSI 不能作为包含 0.7.0 功能的安装包。

## 可复现操作

```powershell
# 完整回归：即使某项失败也继续执行其余项目，最后非零退出。
.\scripts\test-all.ps1 -ValidationDirectory artifacts\validation\phase7-delivery
# 52 项本地健康检查，所有 fixture 都有独立随机目录。
.\scripts\test-health.ps1
# 必须使用无连接/任务、无既有 Agent 的专用用户配置；不满足则拒绝。
.\scripts\test-product.ps1 -Lifecycle
# 真实桌面控件渲染，独立空库；不连接 NAS。
& .\.tools\dotnet\dotnet.exe .\artifacts\bin\CloudDrive.Desktop.dll --smoke
```

源目录、测试工程与脚本均包含在源码 ZIP。交付目录 SHA256SUMS 可验证每个附件。`Sample-EmptyAgent-Health.zip` 来自本次真实空配置 Agent 的快照，**不是 NAS 成功报告**；健康页面截图来自空测试库，Unknown 是预期结果。
