# 0.7.2 SMB 宿主修复与验证

日期：2026-11-23；Windows 11 25H2 build 26200 x64。

## 问题与最小修复

0.7.1 的连接测试可通过 DNS/TCP，但内嵌 PowerShell SDK 的 PS Core 宿主在自己的
配置/授权上下文中拒绝加载 SmbShare.cdxml，同时无法加载 SDK 附带的 Security 类型数据。
同一用户的系统 Windows PowerShell 可正常加载带微软签名的 SmbShare 模块。
仅取消 `InitialSessionState.ExecutionPolicy = Undefined` 也不能修复这一差异。

0.7.2 使用 `PowerShellProcessInstance(5.1)` 与 `CreateOutOfProcessRunspace`，让系统
模块在配套的 Windows PowerShell 中执行。参数仍经 `AddCommand/AddParameter` 绑定，
通过本地重定向管道中的 PSRP 传递；不需要 WinRM 网络监听或远程计算机。
兼容远程序列化后的命令参数元数据，不把其强制转换为本进程 `CommandInfo`。

宿主启动参数固定为 `-Version 5.1 -s -NoLogo -NoProfile`，没有启动脚本、账号、密码、
`-ExecutionPolicy`、runas 或替代可执行程序；模块、映射及凭据冲突处理沿用原流程。
不修改系统/用户/进程执行策略，不移除下载区域标记，不操作他人的 PowerShell 进程或映射。
连接取消时停止自己的管线，释放时关闭自己的宿主。

## 已执行的验证

`scripts/test-smb-host.ps1` 默认不访问 NAS，15 项通过，覆盖：

- 系统宿主绝对路径、固定命令行、隐藏窗口、无额外 UAC、相同 Windows 登录会话。
- 系统 SmbShare 加载、反序列化命令参数识别、只读映射枚举。
- 合成 `PSCredential`/`SecureString` 通过 PSRP 传递且不进入命令行。
- 所有执行策略作用域前后不变、预取消、运行中取消、宿主正常退出。
- 生产 `SmbMapper.CapabilitiesAsync` 识别 `TcpPort` 和 `Credential`。

对用户明确配置并保存的单一 NAS 执行了默认只读连接测试：

| 环节 | 结果 |
| --- | --- |
| DNS/IP | 成功 |
| TCP | 端口可达 |
| Windows SMB 映射与账号认证 | 成功 |
| 共享目录访问和目录列表读取 | 成功 |
| 文件内容读取 / 写入探测 | 未执行 |

测试通过正常连接流程创建本软件映射及 SQLite 租约，不修改 NAS 文件，不断开其他应用映射，
不输出服务器、共享、账号、密码或文件名。测试记录保存在本地
`artifacts/validation/smb-native-host/`，不上传原始用户诊断数据。

## 复现与限制

```powershell
./scripts/test-smb-host.ps1
# 只有明确授权访问已保存连接时才指定此参数；不要在命令行放密码。
./scripts/test-smb-host.ps1 -StorageId '<保存的NAS GUID>'
```

系统实际禁止 Windows PowerShell 或其 SmbShare 模块时仍会失败，不以其他策略重试。
普通 445 不请求管理员权限；自定义端口的 OS/参数/UAC 检查保留，本次未验收自定义端口。
还需继续验证错误凭据、1219、多 NAS、真实占位/水合、大文件、断网/唤醒和 Windows 重登。
此前各阶段的历史失败/未验收报告保留；本次只读连接成功不能替代它们。

本次本地 9 组隔离测试全部通过：unit、connections、smb-host、phase2、phase3、
cache、archive、product、health。4 个真实 NAS 测试工程编译通过，PowerShell/Python
脚本语法检查和公开源码检查通过；测试工程编译不代表已执行真实 NAS 功能验收。
Agent、Desktop、Bootstrap 的标准项目 Release 输出编译通过；运行中的旧客户端不受影响。

实际 Windows CFAPI 缓存测试通过，包含 1 GiB+123 字节的流式水合、释放及再次水合，
数据源为本地 fixture。第三阶段新文件上传的 CFAPI 完成步骤仍复现此前的
`0x8007018B`（拒绝访问云文件），不能声明完整回归通过；该步骤不在本次 SMB 宿主改动范围。
基础原生 CFAPI 的 10 项测试和索引驱动的本地只读水合测试通过。实际 CFAPI 归档测试
完成了 3 个源文件的复制校验、映射提交和源删除，但随后“大文件不触发本地水合”组合
断言失败（内存增量约 1.8 MiB，未超 128 MiB 阈值）；后续断言未执行，归档不计为通过。
这些 CFAPI 测试均使用本地目录，未触及用户 NAS 文件。

旧版客户端仍在运行时不替换其依赖文件；最终 Release 构建、客户端更新启动与公开 CI
的最新结果以本次实际日志及交付说明为准。本文件中的宿主检查和 NAS 连接结果
不代替完整功能回归。
