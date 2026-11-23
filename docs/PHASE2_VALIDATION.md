# 第二阶段交付与验证记录

日期：2026-11-19。项目：`C:\project\CloudDrive Lite`。本记录不把编译通过、生成数据源或局部通过等同于真实 NAS 验收。

## 最终版本汇总

已执行 `scripts/test-all.ps1`。完整输出位于 `artifacts/validation/phase2/`，结果汇总为 `summary.txt`。

| 最终检查 | 结果 |
| --- | --- |
| C++ Release、Core、Storage、Agent、Desktop 构建 | 通过，0 警告、0 错误；C++ `/W4 /WX` |
| 原有命令/路径单元测试 | 30 项通过 |
| 阶段二测试最终重跑 | 被 Windows 应用控制拦截，程序集加载失败 `0x800711C7` |
| 原有真实 CFAPI 集成最终重跑 | 被 Windows 应用控制拦截，WinError 4551 |
| SQLite/管道/CFAPI 索引集成最终重跑 | 被 Windows 应用控制拦截，程序集加载失败 |
| 桌面启动/渲染最终重跑 | 被 Windows 应用控制拦截 |
| 真实 NAS 测试程序构建 | 通过；没有 NAS 配置与凭据，未执行 NAS 操作 |
| 所有 PowerShell 脚本语法与 Python py_compile | 通过 |

最终汇总不是全绿。没有修改或绕过应用控制、PowerShell 执行策略、系统 SMB 安全设置，也没有通过替代加载方式运行被拦截文件。

## 开发过程中实际执行通过的检查

较早构建曾成功运行 `Phase2Tests`，输出 **PASS 42 phase 2 checks**，fixture 位于：

`%TEMP%\CloudDriveLite-tests-b42bba2193304ffc9a51de1585025d58`

覆盖配置/路径边界、连接隔离的稳定 ID、Credential Manager 读写与删除、SQLite 持久化、根重叠保护、602 项递归元数据扫描、256 项索引分页、版本变化拒绝、取消不推断删除、中断代次恢复、完整扫描后标缺失、日志脱敏、密码不入数据库、真实本机关闭端口诊断、RPC 分页、版本化流读取、非零偏移、断开后继续服务。

其中 **1 GiB + 123 字节**来自可寻址生成流，经实际命名管道分块传输并校验内容；该次测得进程私有内存增量约 **1 MiB**，测试上限 128 MiB。这是管道与流实现的有界内存检查，**不是 NAS 或 CFAPI 的 1 GiB 实测**。后续增加的来源配置保护、恢复重建与扫描 restart 处理已编译通过，但最终程序集重跑被策略阻止，不能沿用早期 42 项结果声称最终版本全部已测。

系统 SmbShare 实际参数探测也执行过：内嵌 Runspace 在导入系统 CDXML 模块时，被其有效执行策略拒绝。测试明确输出 BLOCKED 并验证 `SecurityPolicy` 分类，**没有算作 SMB 连接成功**。外部开发 PowerShell 能发现 `TcpPort`、`Credential` 参数，并不能证明应用自己的 Runspace 已能加载模块。

桌面程序较早构建实际启动过并完成三页布局渲染：`artifacts/bin/desktop-smoke.png`、`desktop-smoke.txt`。未通过 UI 点击完成实际 NAS 配置/连接验收。

## 原生 CFAPI 的实际发现

本阶段部分较早构建被系统允许执行，`doctor` 返回 CFAPI 平台信息，C: 为 NTFS、CldFlt 运行中。由此发现并处理了以前未能实际运行到的问题：

1. 普通非云目录查询返回 `ERROR_INVALID_FUNCTION`。保留空目录、无重解析点、非 UPDATE 注册及注册后身份校验，只增加该返回值的处理。
2. 提供程序可见的属性/重解析标签可能被筛选视图清理。状态查询改为以成功的 `CfGetPlaceholderInfo`、本根身份和 OnDiskDataSize 判断，避免显示假阴性。
3. 本地修改后成为普通文件的项，在重启时保留并记录，避免一个脏文件阻断整个命名空间。
4. FETCH_DATA 改为正确处理 RequiredOffset/RequiredLength、4KiB 对齐和 EOF 尾块，保留已有 CfExecute 与取消机制。
5. 原有安全释放在哈希校验后同步等待超时，定位在异步句柄上的 CfDehydrate 调用。当前改为底层已引用 Win32 句柄及独立 OVERLAPPED 完成事件，并保留干净状态、版本、SHA-256、源锁和释放许可保护。**修正后的释放尚未复测通过**，最终构建再次被应用控制拦截。

原有 8 组真实 CFAPI 测试在较早构建中，除释放闭环外其余 7 组通过，包括元数据占位无内容、中文文件真实水合、缓存命中、脏文件释放拒绝、身份/路径边界、停止重启、源变化拒绝与源未修改。保留的运行证据包括 `artifacts/tests/5563ce625c8343f2ae7c70f03079ca16/provider.log` 等独立 fixture。失败根没有自动注销或删除。

索引版真实 CFAPI 测试在 fixture `artifacts/tests/indexed-cd57789daf284ea1a8298a154f9f2dac/` 中实际通过：

- SQLite 分页元数据生成真实 CFAPI 占位，OnDiskDataSize 为 0。
- `FETCH_DATA → SQLite → Agent pipe → 只读源流 → CfExecute` 完成 3 MiB + 123 字节中文/特殊文件名的 SHA-256 校验。
- 普通本地写入被只读属性拒绝。

该次测试的释放先遇到测试自身未释放读取流造成的共享冲突，测试已修正 using 作用域。之后最终重跑受策略阻止，**不能将该集成标为完整通过**；数据源是本地 fixture，不是真实 SMB。

## 用户要求的真实 NAS 矩阵

| 项目 | 当前证据/待验收内容 |
| --- | --- |
| 正确445连接 | 实现已构建；未提供 NAS/凭据，未测 |
| 自定义端口 | OS/参数/UAC 同用户边界已实现；实际管理员映射与 NAS 未测 |
| 错误账号密码 | 原生错误分类测试通过；真实 NAS 错误码返回未测 |
| 错误共享/子目录 | 分类与路径保护已测；真实 NAS 未测 |
| 多 SMB 连接 | 配置与连接 ID 隔离已测；多个真实服务器/1219 未测 |
| 远端扫描元数据 | 本地只读源602项/分页/取消恢复已测；真实 SMB 未测 |
| 本地占位不下载 | 索引版真实 CFAPI 局部通过；最终重跑被策略阻止 |
| 双击按需读取 | 同一 CFAPI 数据链程序读取局部通过；Explorer 实际双击和 NAS 未测 |
| 释放本地空间 | 早期同步等待超时；OVERLAPPED 修正后未复测通过；NAS 未测 |
| SMB断网保留索引/缓存 | 扫描取消与 RPC 断开保留已测；NAS 断电和完整缓存离线读取仍需验收 |
| SMB恢复 | 退避、网络事件、仅重建自有映射代码已构建；真实断网恢复未测 |
| 1GB以上文件 | 1GiB+123 管道测试通过；真实 NAS/CFAPI大文件和并发内存曲线未测 |
| 中文/特殊文件名 | 本地扫描、索引、管道和真实CFAPI局部通过；真实NAS未测 |
| Windows重启 | 持久配置/索引及登录启动选项已实现；真实重新登录、UAC与重启未测 |

## 可复现入口与交付文件

构建与全部自动化：`scripts/build.ps1`、`scripts/test-all.ps1`。单独执行：`test-unit.ps1`、`test-phase2.ps1`、`test-integration.ps1`、`test-indexed-cfapi.ps1`。实际 NAS：`test-smb-nas.ps1`，只接收已保存连接 GUID、测试文件相对路径和预期 SHA-256，不接收密码。

源码入口：

- `src/CloudDrive.Storage/WindowsSmb.cs`：Runspace、能力检查、映射租约、分阶段诊断。
- `CredentialVault.cs`：Windows Credential Manager。
- `Models.cs`：配置、稳定 ID、错误分类和第三阶段上传接口边界。
- `IndexStore.cs`、`Scanner.cs`：SQLite、批处理、检查点、完整扫描删除边界。
- `SmbBridge.cs`、`AgentRuntime.cs`：版本化数据块、取消/超时、进度、恢复及进程寿命。
- `src/CloudProviderHost/SmbBridge.h` 与 `main.cpp` 的增量：批量占位、索引身份、范围水合和受保护释放。
- `src/CloudDrive.Desktop/`：三页界面、同用户 UAC 专用 Agent 和登录启动项。
- `src/CloudDrive.Agent/SmbCommands.cs`：SMB CLI 与无密码命令行入口。

当前限制、数据流、只读边界、未来上传接口以及依赖系统策略的部署条件见 `PHASE2.md`。本阶段不能宣称已完成真实 NAS 全量验收或已经支持安全双向编辑。
