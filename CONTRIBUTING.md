# 参与开发

感谢参与 CloudDrive Lite。提交贡献即表示你有权将所贡献的代码按项目的
[MIT 许可证](LICENSE)发布；保留已有第三方作者的版权和许可证，不提交来源不明的代码。

请先阅读 [README](README.md)、[架构](docs/ARCHITECTURE.md)和
[当前验证结果](docs/CONNECTIONS_VALIDATION.md)。项目处于开发阶段，完整双向同步、
真实 NAS、安装升级及正式签名尚未验收。不要把构建通过描述成产品验收完成。

1. 从 `main` 新建分支，说明具体问题及预期行为；修改已验证的 CFAPI 核心前先讨论方案。
2. 在 Windows x64、NTFS、PowerShell 7、.NET 10 SDK、MSVC 和 Windows SDK 环境构建。
3. 运行 `./scripts/check-public-source.ps1`、`./scripts/build.ps1 -Configuration Release`
   及修改所涉及的测试。完整本机回归入口为 `./scripts/test-all.ps1`。
4. Pull request 说明改动、验证结果及未覆盖场景。故障注入、本地 fixture 与真实 NAS
   测试必须分别标注；NAS 测试仅使用自己授权的专用测试目录。

不得提交密码、令牌、私钥、数据库、连接备份、原始诊断日志、用户文件或本机工具链。
公开示例使用虚构数据。日志/截图发布前检查服务器地址、用户名和文件路径。

公共 CI 只构建并执行不依赖真实 NAS/CFAPI 的测试，不接收 NAS 凭据，也不执行提权、
安装、签名或发布。来自外部贡献者的修改需经维护者审核；CI 不使用 `pull_request_target`。
维护者应在 GitHub 和未来的签名服务启用多因素认证。
