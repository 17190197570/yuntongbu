# 第六阶段开发交付与验证

日期：2026-11-20，Windows11 build26200 x64、当前用户、.NET10。用户无NAS，尚无可信签名证书和HTTPS更新发布站点；按用户确认交付未签名开发包，正式发布未验收。

## 最新全量结果

命令：`scripts/test-all.ps1 -ValidationDirectory artifacts/validation/phase6-delivery`，退出1。汇总：[summary.txt](../artifacts/validation/phase6-delivery/summary.txt)，控制台：[delivery-console.log](../artifacts/validation/phase6/delivery-console.log)。

| 项目 | 最新结果 | 证据与范围 |
|---|---|---|
| Release构建 | 通过，零警告/错误 | [build-release.log](../artifacts/validation/phase6-delivery/build-release.log)，原生+Agent+Desktop+Bootstrap |
| 基础单元 | 30项通过 | [unit.log](../artifacts/validation/phase6-delivery/unit.log) |
| 第二阶段 | 42项通过 | [phase2.log](../artifacts/validation/phase6-delivery/phase2.log)，含本地桥1GiB范围读；SmbShare现有执行策略限制被尊重，不等于真实SMB连接成功 |
| 第三阶段 | 23项替代检查通过，实际Host被阻止 | [phase3-new-upload.log](../artifacts/validation/phase6-delivery/phase3-new-upload.log)，不是实际转换通过 |
| 缓存 | 38项fixture通过，实际Host被阻止 | [phase4-cache.log](../artifacts/validation/phase6-delivery/phase4-cache.log) |
| 归档 | 56项本地文件I/O/替代保护检查通过，实际Host被阻止 | [phase5-archive.log](../artifacts/validation/phase6-delivery/phase5-archive.log) |
| 第六阶段 | **29项通过** | [phase6-product.log](../artifacts/validation/phase6-delivery/phase6-product.log)，含最后新增v3 ZIP拒绝 |
| 原生CFAPI / 索引CFAPI | 被应用控制阻止启动 | [cfapi-local.log](../artifacts/validation/phase6-delivery/cfapi-local.log)、[cfapi-indexed.log](../artifacts/validation/phase6-delivery/cfapi-indexed.log) |
| 桌面冒烟 | 通过 | 八页、配置窗口、真实Close隐藏及Show重开；截图保存在[交付验证目录](../artifacts/validation/phase6-delivery/) |
| 4类NAS harness | 仅构建通过，未执行NAS | 同目录nas-*-harness-build.log |
| PowerShell/Python语法 | 通过 | 汇总script-syntax；后新增打包脚本另行核验 |

当前Host错误是WinError4551“应用程序控制策略已阻止此文件”。最后生命周期专项的29项常规检查通过后，Bootstrap.dll加载被代码完整性策略阻止（0x800711C7），因此专项退出1：[product-final-lifecycle.log](../artifacts/validation/phase6/product-final-lifecycle.log)、[代码完整性事件](../artifacts/validation/phase6/lifecycle-code-integrity.json)。没有改变安全策略、改名或复制加载来规避。

## 较早成功记录与实际失败

同日16:37–16:39的[全量记录](../artifacts/validation/phase6-final/summary.txt)中，缓存38+19、归档56+10、原生CFAPI10组、索引CFAPI及桌面均通过，唯一失败是第三阶段真实转换0x8007018B。该轮没有应用控制阻止；不能将其覆盖成最终全绿，也不能因为后来的系统阻断而抹去实际运行证据。

[归档实际CFAPI记录](../artifacts/validation/phase6-final/phase5-archive.log)包含物理目录重叠保护、1GiB+123字节复制/校验/映射/删源、原占位路径新目标水合与释放重读。源与目标均为本地目录，**不是NAS**。托管测试进程归档前后内存差3,227,648字节，不是峰值，也不包含Host或Windows缓存。

[较早产品生命周期](../artifacts/validation/phase6/product-lifecycle.log)完成28项常规+4项真实空Agent测试，共32项：独立Bootstrap启动、重复入口不产生第二Agent、只终止测试拥有的空Agent后自动恢复、正常退出不被重启。最后源码增加顶层异常日志及v4恢复限制后，本机Bootstrap被应用控制阻止，**不能声称最终33项生命周期已通过**。

0x8007018B来自较早实际CfConvertToPlaceholder，尚未修复；已有文件修改上传未实现。当前继续只读，不将任务书“前五阶段已完成”直接当作验收事实。

## 产品化检查覆盖

29项覆盖真实当前用户命名管道、白名单命令拒绝、设置原子更新/坏配置拒绝、退出退避预算、SQLite汇总、在线备份、ZIP精确三条目、旧版本/损坏/穿越拒绝、离线恢复前备份/自动写入暂停、拒绝丢弃较新任务、日志脱敏与刷盘、诊断不含账号/地址、受管状态卸载阻断、RSA3072清单验签/篡改拒绝/有效期/渠道/HTTPS/域名/序号，以及真实WinVerifyTrust拒绝无签名PE，即使SHA-256正确。

签名证书正向、完整HTTPS下载/断点/安装升级、不同用户攻击IPC、强杀有活动文件的Agent/Host、Windows登录注销及重启未验收。没有为了测试终止业务进程。

## 包与文档

`artifacts/Release/CloudDriveLite-x64.msi`为当前用户win-x64自包含开发MSI，版本0.6.0，NotSigned。`scripts/test-package.ps1`完成12项只读MSI元数据检查：版本、per-user范围、安装目录、3个安全维护动作、升级保留条件、内置运行时及4个EXE。日志：[package-check.log](../artifacts/validation/phase6/package-check.log)。**未执行实际安装、修复、升级、失败回滚或卸载。**

`CloudDriveLite-Source.zip`包含源代码、测试、脚本、文档和安装定义，排除bin/obj、工具链和真实信任配置。`SHA256SUMS.txt`覆盖交付文件。`InstallGuide.pdf`和`RecoveryGuide.pdf`各2页，全部4页已用Poppler渲染并逐页目视确认中文及分页；校验和、静态元数据和文档排版都不能代替产品测试。

## 未完成和未验收

- 非空根卸载方案A/B **尚未实现**；仅检查并阻断危险移除。取消时保留程序、任务、占位和NAS。
- 完整Fluent/WinUI3、动态图标、全部首页指标尚未实现；当前统一WinForms。
- 既有文件编辑回传及第三阶段真实转换仍未完成，归档后编辑回传也不可用。
- 无真实NAS：445、自定义端口、凭据冲突、多NAS、断网、会话失效、NAS重启、归档后远端读写均未验收。
- 无证书/站点：正式签名、可信更新正向、时间戳、发布与旧版包保管流程未验收。
- 无干净VM：首次安装、修复、低版升级、高版回退、失败回滚、卸载矩阵未验收。
- 进程恢复主要基于退出；独立心跳超期/挂起检测、数据库健康检查调度与完整模块诊断留待第七阶段。
- 不同用户/会话、10万文件、并发NAS、24/72小时内存和句柄、休眠唤醒未验收。

## 复现

```powershell
.\scripts\build.ps1 -Configuration Release
.\scripts\test-product.ps1
# 仅允许无连接/任务且无既有Agent的独立空环境：
.\scripts\test-product.ps1 -Lifecycle
.\scripts\test-all.ps1 -ValidationDirectory 'artifacts\validation\phase6-recheck'
.\scripts\package.ps1 -SkipBuild
.\scripts\test-package.ps1
```

签名脚本仅接受CurrentUser证书指纹和HTTPS时间戳地址；不要传私钥/密码。真实NAS测试须使用专用连接和数据，说明见各阶段文档。本机策略阻断时保留日志，使用组织认可的签名/测试环境继续验收。
