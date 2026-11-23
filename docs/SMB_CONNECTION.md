# SMB 连接设计（历史设计及第二阶段补充）

> 当前 0.7.1 已拆分 NAS 配置与文件夹映射，见 [CONNECTIONS.md](CONNECTIONS.md)。以下包含历史设计。

> 第二阶段源代码已实现，当前流程和验收限制见 [PHASE2.md](PHASE2.md)。下文“尚未实现”描述第一阶段当时的状态。

当前版本只用本地目录验证 CFAPI。本文约束下一阶段，不表示原型已经能够连接 NAS。SMB 必须经 Windows 原生 SMB Redirector；不以 WebDAV、SMBLibrary 或对用户直接暴露的映射盘替代云文件根。

## 配置与路径

每个连接保存服务器地址、1–65535 范围的端口（默认 445）、共享名、远端子目录、用户名、可选域、凭据引用和本软件映射的所有权记录。端口作为配置传给 SMB 映射层；不能拼接成 `\\server:1445\share`。

```text
服务器：192.168.1.100
端口：1445
共享：Public
子目录：Documents
用户名：nas-user

共享 UNC：\\192.168.1.100\Public
文件根：共享映射 + Documents
```

共享映射和 CFAPI 本地同步根是不同目录。用户通过本地 NTFS 云文件根访问文件；盘符只是软件内部连接资源。

## 替代 TCP 端口与参数核对

Microsoft 文档给出的替代 TCP 端口客户端要求包括 Windows 11 24H2 或 Windows Server 2025 等受支持系统，并要求管理员权限。实现时还需检测当前 `New-SmbMapping` 是否实际具有 `TcpPort` 参数，避免仅按版本字符串推断可用。旧版 Windows 的首期保证范围为标准 445；不应在不支持的系统上忽略用户填写的替代端口后悄悄使用 445。

原始需求中的示例需要一个参数类型修正：`New-SmbMapping -Password` 在 Microsoft 文档中的类型是 `String`，不是 `SecureString`；`-TcpPort` 是 `UInt16`。因此不能把 `$securePassword` 直接当作已核实可用的参数契约。

受控 Runspace 的设计应使用 `PowerShell.AddCommand("New-SmbMapping")` 和类型正确的 `AddParameter`，而不是把地址、用户名或密码拼接成脚本文本。安全凭据在调用边界临时解密；如果 cmdlet 要求托管字符串，就必须明确这会产生短生命周期明文对象，不能声称 `SecureString` 让该边界完全没有明文。

管理员映射与普通用户 Agent/Host 之间还存在登录会话可见性问题。提升权限创建映射之后，必须在实际运行 Agent 的会话中验证可访问性；不能只凭管理员终端连接成功判定整个方案打通。

## 凭据与认证

- 用户显式输入账号、密码，支持 NAS 本地账号、工作组和域账号，不依赖当前登录身份，也不要求预先手工映射网络盘。
- 使用 Windows Credential Manager 或 DPAPI 保存；SQLite 只存凭据引用。
- 不在命令行、日志、配置明文、异常细节或 PowerShell 转录中记录密码；日志采用允许列表字段和脱敏结果。
- 支持修改密码、删除保存凭据、认证失败后重新输入。连接完成后尽快释放敏感数据引用。
- Windows 原生客户端可能对同一服务器的不同凭据产生会话冲突（包括系统错误 1219）。不能通过删除其他软件的会话或映射来强行恢复。

## 连接测试的分层结果

按照地址与端口校验、TCP 连通、SMB 映射及认证、共享/子目录、读取和写入能力分别报告结果。TCP 连接成功只证明目标端口可达，不证明 SMB 协商或认证成功；SMB 的部分错误不能仅凭一个通用 Win32 错误码准确区分，界面应保留适当的“SMB 会话建立失败”结果并附脱敏诊断码。

结果至少覆盖网络不可达、端口不可连接、协议/会话失败、认证失败、共享或子目录不存在、只读和完整成功。测试需显示实际服务器与端口。

读取测试只访问安全目标。写权限验证使用软件创建的唯一临时测试目录/文件，结束时只清理本次创建且确认归属的资源，绝不能创建、替换或删除同名用户文件。无法清理时记录残留，不扩大删除范围。

## 多连接与恢复

为每个连接分配可用盘符并记录所有权；创建时再次确认盘符未被占用。断开、删除和退出清理必须同时核对本软件记录与操作系统实际映射，禁止仅凭盘符执行移除。

Agent 与 Host 运行于能访问映射的当前用户会话中。网络中断或超时将连接标为离线，任务保留并按配置重试；NAS 重启和凭据过期需重新建立会话，不能误报上传成功或释放未同步内容。自定义端口修改后必须建立新连接并重新测试。

## 安全要求

禁用 SMB1；优先 SMB 3.x 和签名，可配置服务器支持时要求加密。替代端口不会自行增加加密，也不能让只监听 445 的服务器自动接受其他端口。跨公网先通过 VPN 等安全网络连接，不将直接暴露公网 445 作为产品默认方案。

## Microsoft 参考

- [New-SmbMapping 参数与类型](https://learn.microsoft.com/en-us/powershell/module/smbshare/new-smbmapping?view=windowsserver2025-ps)
- [配置替代 SMB 端口](https://learn.microsoft.com/en-us/windows-server/storage/file-server/smb-ports)
- [Windows 错误码 1200–1299](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-)
# 第二阶段实现

连接管理、Credential Manager、Runspace 参数绑定、映射租约和受控 UAC 入口已加入源码。当前实现及真实 NAS 尚未覆盖的测试见 [PHASE2.md](PHASE2.md) 和 [PHASE2_VALIDATION.md](PHASE2_VALIDATION.md)。以下保留设计约束与官方资料。
