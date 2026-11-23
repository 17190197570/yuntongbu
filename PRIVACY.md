# 隐私说明

适用于当前公开源码版本 0.7.1，日期：2026-11-23。

CloudDrive Lite 不包含向项目维护者发送文件、遥测或诊断的服务端接口。
程序会按照用户配置与 NAS/SMB 存储通信；域名解析、Windows SMB 认证和凭据协商由
Windows 处理，具体网络行为还受系统、域和 NAS 配置影响。

## 本机保存的信息

- NAS 地址、共享名、工作组、用户名、文件夹映射、文件元数据、任务、缓存及健康状态
  保存在当前 Windows 用户的 `%LOCALAPPDATA%\CloudDriveLite` 下。
- NAS 密码通过 Windows Credential Manager 保存，不写入 SQLite，也不拼接到命令行。
- 水合后的文件内容位于用户选择的本地映射目录；数据库、备份和缓存不应公开上传。
- 任务日志在本机保存。软件会尝试脱敏敏感字段，但原始日志不应被视为可以直接公开。
  用户主动导出的诊断文件应在提交给他人前再次检查。

## 对外连接

SMB 访问、DNS 查询、自动重连及用户授权的归档迁移使用用户设置的目标存储。
自动重连可在后台持续执行；启用功能前应确认 NAS 目标和工作目录。

更新检查默认关闭。只有用户启用并提供通过信任校验的 HTTPS 更新配置后，软件才会
访问该更新服务器。当前开发版本没有预置可用的正式更新渠道。服务器可能记录网络请求，
该服务器的运营者应另行提供隐私说明。

编译源码时，.NET/NuGet 和 GitHub Actions 会访问其软件源/服务；申请 SignPath 签名时
也会使用其服务。这些开发/发布活动不等同于桌面客户端向它们上传 NAS 凭据或文件。
相关服务的隐私说明由服务提供者发布：

- GitHub: https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement
- Microsoft: https://privacy.microsoft.com/privacystatement
- SignPath: https://about.signpath.io/privacy-policy

## 用户控制

可在界面中暂停任务、关闭登录启动/自动连接/更新检查。移除映射或卸载前应遵循
[安装说明](docs/INSTALL_GUIDE.md)与[恢复说明](docs/RECOVERY_GUIDE.md)，确保在线占位文件
仍可读取。不要直接删除数据库或受管同步根来达到“清理隐私”的目的。

尚未完成独立隐私审计；第三方依赖和 Windows 本身的网络行为应结合各自说明评估。
