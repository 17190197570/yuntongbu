# 本次验证记录

> 以下为第一阶段历史记录，保留原始结果。第二阶段的实际执行、部分运行通过与当前阻塞见 [PHASE2_VALIDATION.md](PHASE2_VALIDATION.md)。

本记录区分代码构建、已执行测试和未完成的运行验收，不把编译成功当成 CFAPI 闭环验证。

## 环境

- Windows 11 专业版，Build 26200，当前登录用户，非管理员。
- C: 为 NTFS；系统 Cloud Files 筛选驱动 `cldflt` 已运行。
- 项目内 .NET SDK 10.0.404。
- 微软签名 MSVC x64 19.44.35230，Windows SDK 10.0.26100.0 头文件和库。
- 构建工具保存在 `.tools`，未更改 Windows 应用控制策略。

## 已通过

| 项目 | 结果与证据 |
| --- | --- |
| C++ Release 编译与链接 | 成功；`/W4 /WX`，真实链接 `CldApi.lib`、`Bcrypt.lib` |
| C# Agent / Core 构建 | 成功，0 警告、0 错误 |
| .NET 单元测试 | **30 项通过**；命令解析、Unicode、路径边界、根重叠、路径穿越、备用数据流和歧义路径 |
| 构建脚本语法 | PowerShell 解析通过 |
| 集成测试脚本语法 | Python `py_compile` 通过 |
| 原生 manifest | 已从生成的 EXE 提取，确认嵌入 `longPathAware=true`、`asInvoker` 和 Windows 兼容声明 |

日志位于 `artifacts/validation/build-release.log`、`build-unit.log`、`test-unit.log`，嵌入清单位于同目录 `embedded.manifest`。当前产物为 `artifacts/bin/CloudDrive.Agent.exe` 和 `CloudProviderHost.exe`。

## 尚未通过的运行门槛

`scripts/test-integration.ps1` 已实际尝试启动最终的原生程序，但 Windows 应用控制在进程入口之前返回：

```text
BLOCKED/FAILED: [WinError 4551] 应用程序控制策略已阻止此文件。
```

本次集成运行没有进入原生提供程序，没有创建 CFAPI 注册或占位，**不能声称占位、水合、释放或重启已实测通过**。集成输出保存在 `artifacts/validation/test-integration.log`；随机测试源和空目标目录保存在 `artifacts/tests/3d2a988f806a4da5a73b9c757e7dbe16`，便于复核。

最初 .NET 测试宿主也被策略拦截；最终构建后的单元测试已经实际执行并通过，以最终 `test-unit.log` 为准。原生 EXE 的执行限制仍然存在。未尝试关闭策略、增加排除项或绕过拦截。

下一门槛是在允许开发程序执行的受信任开发环境，或采用组织认可的代码签名流程后，重新运行集成测试，然后人工验证资源管理器图标与双击。仅有 CFAPI 库可用和驱动运行，不足以证明完整体验通过。

## 必须保留的未验证项

- 实际 CFAPI 注册、元数据占位、完整水合、脱水回调协调和释放后的磁盘占用。
- 本地修改、源版本变化、独占句柄、固定状态、停止重启等原生运行保护。
- Explorer 云朵图标、文件关联应用双击和导航栏集成。
- 超过 4 GB 文件、长路径、大目录、并发取消、休眠与唤醒、磁盘空间不足。
- SMB、自定义端口、安全凭据、上传与冲突、自动清理和归档：尚未实现。
