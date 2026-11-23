# CloudDrive Lite

Windows 原生按需云文件工具。最终路线为 **Windows CFAPI + Windows 原生 SMB Redirector + C#/.NET + C++**。

2026-11-23：当前源码 **0.7.2** 修复 SMB 连接测试的 PowerShell 宿主兼容问题：
通过系统 Windows PowerShell 的本地 PSRP 参数通道调用 `New-SmbMapping`，不修改执行策略，
不在命令行传递密码。已验证用户保存的真实 NAS 的认证、共享访问和目录列表读取；
未执行文件内容读取或写入探测。见 [SMB 宿主验证](docs/SMB_HOST_VALIDATION.md)。

项目现以 [MIT 许可证](LICENSE)开源，公开仓库为
[17190197570/yuntongbu](https://github.com/17190197570/yuntongbu)。
这是开发中的 Windows 文件提供程序，完整双向同步和生产部署尚未验收，请使用测试数据。
依赖保留各自许可证，见 [第三方说明](THIRD_PARTY_NOTICES.md)。

**Code signing policy：** 计划申请 SignPath Foundation 免费开源签名，当前**未提交、未获批、未签名**。
新项目仍需满足公开发布、可核实信誉及审核要求；开源后不会立即解除 Windows 应用控制拦截。
详见[代码签名策略](docs/CODE_SIGNING.md)、[申请材料草稿](docs/SIGNPATH_APPLICATION.md)、
[隐私说明](PRIVACY.md)、[安全报告](SECURITY.md)与[贡献指南](CONTRIBUTING.md)。

公开 CI 在 Windows 上编译并运行隔离 fixture 测试，只上传验证日志，不连接真实 NAS，
不签名、不发布安装包；真实 CFAPI/资源管理器、NAS、安装生命周期仍需单独验收。
源码发布前执行 `scripts/check-public-source.ps1`；可用 `scripts/package-open-source.ps1`
生成排除工具链、凭据及本地数据的源码包，保留此前封存开发包。

2026-11-20：当前源码 **0.7.1 / SQLite v6** 将 [NAS 连接与文件夹映射](docs/CONNECTIONS.md)拆成两页。NAS 页仅保存地址、端口、共享、用户名/密码和工作组；一个 NAS 可复用到多个本地/远端目录映射。支持离线保存映射与显式创建缺失目录，保留旧 CFAPI 标识及数据。两页采用卡片、必填提示与主次按钮，Ant Design MCP 在本会话未接入。最新验证见 [连接/映射验证](docs/CONNECTIONS_VALIDATION.md)；历史交付包不变。

2026-11-20：此前源码已增量加入[第七阶段运行健康中心](docs/PHASE7.md)，版本 **0.7.0**、SQLite v5。包含独立健康调度、六种状态、实际 Windows/IPC/CFAPI/SMB/SQLite 检查、异常合并、分级修复与独立复检、通知去重及脱敏 JSON/HTML/ZIP。第九页为“运行健康中心”。[验证与未完成清单](docs/PHASE7_VALIDATION.md)区分真实 Windows、注入测试、NAS 及系统策略阻断。第六阶段 MSI 仍是单独封存的 0.6.0，不含第七阶段。

2026-11-20：[第六阶段开发包](docs/PHASE6.md)加入独立 Bootstrap/Agent、系统托盘、当前用户登录启动、八页桌面、WiX MSI、可信更新校验、在线备份/恢复和脱敏诊断。版本 **0.6.0**，交付位于 `artifacts/Release`。用户确认尚无证书/更新站点，先交付未签名开发包；正式发布未验收。安装/升级/卸载 VM 测试、非空根卸载方案 A/B、完整双向同步和真实 NAS 仍未完成。最新全量检查受到 Windows 应用控制阻断，历史成功与最新结果分列在[第六阶段验证报告](docs/PHASE6_VALIDATION.md)，不能用构建通过替代产品验收。

当前已在第一阶段工程上加入**第二阶段：SMB 连接、SQLite 元数据索引、Agent 管道、CFAPI 批量占位/按需读取与基础图形界面**。第二阶段入口保持远端只读。真实 NAS、UAC 和 Windows 会话恢复的验收状态见 [第二阶段说明](docs/PHASE2.md) 与 [第二阶段测试结果](docs/PHASE2_VALIDATION.md)。

2026-11-20：正在按顺序开发[第三阶段首项：新增上传试验](docs/PHASE3.md)。已加入增量迁移、持久任务、临时文件校验提交、恢复日志及同步中心，默认关闭；新增成功后转只读。**尚未通过真实 NAS 验收，不能视为已支持双向同步。** [本次验证记录](docs/PHASE3_VALIDATION.md)包含系统阻断及既有释放失败。

2026-11-20：新增[第五阶段归档迁移](docs/PHASE5.md)，含严格创建/修改时间 AND 预览、复制/双端 SHA-256/时间保留、物理映射切换、安全删源、持久恢复、周/月调度和归档页面。自动归档默认关闭。真实双 NAS 与归档后编辑回传未完成，不能作为第五阶段完整验收。见[第五阶段验证](docs/PHASE5_VALIDATION.md)。

第四阶段[缓存管理](docs/PHASE4.md)包含实际分配统计、访问记录、三类策略、固定/释放及持久任务，自动清理默认关闭。旧释放超时已在真实本地 CFAPI 回归中消失；真实 NAS 仍未验收，新增上传实际转换仍未通过。[第四阶段验证](docs/PHASE4_VALIDATION.md)保留历史结果；当前全量日志默认写入 `artifacts/validation/phase7-current`，本次封存验收位于 `phase7-delivery`。

启动 SMB 界面：`scripts/start-smb.ps1`；所有自动化检查：`scripts/test-all.ps1`。下文保留本地模拟入口，便于回归第一阶段。

## 构建与演示

需要 Windows 10 2004 或更新版本、x64、本地 NTFS、.NET 10 SDK，以及 Visual Studio C++ Build Tools（MSVC v143、Windows SDK）。推荐在 **x64 Native Tools** 开发终端中使用 PowerShell 7。项目也能发现本机已经准备好的 `.tools` 便携工具；该目录不入版本控制。

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\start-demo.ps1
```

构建产物位于 `artifacts\bin`。演示源位于 `.demo\source`，资源管理器中打开 `.demo\cloud`。首次启动同步根必须为空；在另一个终端或资源管理器读取文件时触发 `FETCH_DATA`，按 1 MiB 缓冲流式传输，平台采用完整文件水合策略。保持提供程序运行，Ctrl+C 安全断开；注册与占位会保留。

```powershell
$Agent = Join-Path $PWD 'artifacts\bin\CloudDrive.Agent.exe'
# 仅使用项目内便携 SDK 时需要设置；安装系统 .NET 10 时无需此行。
$env:DOTNET_ROOT = Join-Path $PWD '.tools\dotnet'
& $Agent doctor
& $Agent status --root "$PWD\.demo\cloud" --path '欢迎.txt'
& $Agent release --root "$PWD\.demo\cloud" --path '欢迎.txt'
```

也可以使用 `run --source <本地绝对路径> --root <本地绝对路径>` 启动独立测试。源和根不能重叠，也不能放在其他云同步根或 junction/symlink 内。阶段一不支持运行中刷新源版本；源内容发生变化时会拒绝使用旧身份水合和释放。

**请使用测试文件。** 原型不上传本地修改。释放前要求干净、未固定、未被占用，并在独占保护期间比对源和本地 SHA-256；不会强制把修改标为已同步。源目录只读访问。`unregister --root ...` 仅允许已停止且为空的本软件同步根，清空前需要自行保存本地修改。原型尚未注册 Explorer 导航栏入口；状态图标和双击体验需人工验收。

## 验证

```powershell
.\scripts\test-unit.ps1
.\scripts\test-integration.ps1
```

单元测试检查命令和路径边界；集成测试使用真实 CFAPI，在 `artifacts\tests\<随机目录>` 创建独立源与同步根，检查元数据占位、跨缓冲区中文文件水合、缓存、释放后重读、本地修改保护、源变化拒绝和停止重启。成功时只清理本次生成的本地测试条目并注销空根；失败时保留源、根与日志。超过 4 GB、Explorer 图标、实际双击、并发取消、休眠及磁盘不足仍需补充验收。

当前机器的构建与执行记录见 [验证记录](docs/VALIDATION.md)。编译成功不代表运行测试通过；如果 Windows 应用控制阻止未签名的开发程序，需要在组织认可的开发环境或签名流程中继续验收。

## 代码与后续顺序

```text
src/CloudDrive.Core/       命令模型与参数边界
src/CloudDrive.Bootstrap/  当前用户进程启动、退避恢复与更新安装入口
src/CloudDrive.Agent/      当前用户会话中的 C# 控制进程
src/CloudDrive.Storage/    SMB、凭据、SQLite、扫描、管道及恢复
src/CloudDrive.Desktop/    连接管理、存储状态、同步中心、缓存管理、归档管理、任务日志
src/CloudProviderHost/     C++ CFAPI 回调、占位、水合与安全释放
tests/UnitTests/          无第三方依赖的 .NET 测试程序
tests/IntegrationTests/   真实 CFAPI 集成测试
scripts/                 构建、演示与测试入口
docs/                    原始需求、架构、接口与验收计划
```

下一步在实际 NAS 和受信任执行环境完成基础功能、缓存及归档验收，并修复新增上传转换，再按顺序开发修改上传、重命名、删除、远端变化及冲突处理。可信更新校验框架已加入，尚无正式证书和发布渠道，签名正向及真实升级未验收。

- [完整原始需求](docs/REQUIREMENTS.zh-CN.md)
- [第七阶段健康中心源码与安全边界](docs/PHASE7.md)
- [第七阶段故障注入和自动恢复验证](docs/PHASE7_VALIDATION.md)
- [架构与分阶段边界](docs/ARCHITECTURE.md)
- [CFAPI 行为与限制](docs/CFAPI.md)
- [SMB 连接设计与端口约束](docs/SMB_CONNECTION.md)
- [测试计划](docs/TEST_PLAN.md)
- [第六阶段源码与运行结构](docs/PHASE6.md)
- [第六阶段验证及未完成项目](docs/PHASE6_VALIDATION.md)
- [Windows兼容性与发布门槛](docs/PHASE6_COMPATIBILITY.md)
- [开发包安装使用](docs/INSTALL_GUIDE.md)
- [备份与故障恢复](docs/RECOVERY_GUIDE.md)
