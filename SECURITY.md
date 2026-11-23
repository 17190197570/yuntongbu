# 安全问题报告

当前版本为开发版本，尚未完成真实 NAS、完整双向同步和正式发布验收。
请使用测试数据并保留独立备份。

请通过本仓库 GitHub 的 **Security → Advisories → Report a vulnerability** 私密报告
可利用的漏洞，不在公开 issue 中提交密码、NAS 地址、私钥、用户文件或未脱敏日志。
该入口需要仓库所有者先启用 Private vulnerability reporting；若当前不可用，请在普通
issue 中仅请求一个私密联系渠道，不公开漏洞利用细节。

报告应包含受影响版本、经过脱敏的最小复现、预期/实际行为、Windows 版本和潜在影响。
项目尚无固定响应时限或漏洞奖励计划。

维护者应检查凭据隔离、IPC 权限、路径穿越/重解析点、缓存释放、归档删除与更新信任链。
不得以关闭 Windows 应用控制、关闭证书校验或在 PowerShell 设置 Bypass 来修复问题。
