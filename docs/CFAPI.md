# CFAPI 本地原型

第七阶段 0.7.0 将 Host 的 READY/HEALTH 时间单独保存为 HeartbeatAt，SMB 观察不再刷新 CFAPI 心跳。健康检查分别核对根身份、Host PID、连接报告与心跳过期；重复水合失败依据有限时间窗/日志样本，未观察到的外部/UAC Host 为 Unknown。检查不注册根、不触发本地正文读取、不强杀 Host；具体恢复仍经过原生命周期安全收尾。详见 [第七阶段健康中心](PHASE7.md)。下段“共用字段”描述是封存 0.6.0 的历史限制。

第六阶段继续复用占位、水合、释放和Agent数据桥。原生Host仅增加每10秒的 `CfQuerySyncProviderStatus` 健康报告；Agent跟踪PID、READY、SMB状态及退出，Bootstrap监控Agent退出并限次退避。Host完全结束、根连接锁释放后才能重启；没有强杀卡住的Host。健康报告时间与SMB恢复共用字段，目前不能独立判定心跳过期或证明“永远连接正常”。详见 [第六阶段](PHASE6.md)及[最新测试与限制](PHASE6_VALIDATION.md)。非空根注销/转普通文件尚未实现，开发版阻止有受管根时卸载。

2026-11-20：第五阶段不修改原生占位身份或根策略；保留逻辑文件版本，在 Agent 的 FETCH_DATA 数据桥中按 FileStorageMappings 路由到归档目标，缓存释放使用同一路由。归档的本地保护采用独占 CFAPI oplock 与受保护引用，锁定期间从已有句柄查询状态，不重新打开本地文件。见 [PHASE5.md](PHASE5.md) 与 [PHASE5_VALIDATION.md](PHASE5_VALIDATION.md)。

缓存管理补齐 `AUTO_DEHYDRATION_ALLOWED`，保留水合 FULL/目录填充 ALWAYS_FULL，对精确拥有的旧根原地更新。旧释放超时已在本地真实 CFAPI 回归中解决；固定、释放、访问提示见 [PHASE4.md](PHASE4.md)，该阶段历史验收见 [PHASE4_VALIDATION.md](PHASE4_VALIDATION.md)。下文含历史背景。

> 以下保留第一阶段设计背景。第二阶段已扩展索引分页、批量占位和 SMB 管道范围读取；当前实现及未通过的运行验收见 [PHASE2.md](PHASE2.md) 与 [PHASE2_VALIDATION.md](PHASE2_VALIDATION.md)。

## 运行条件与入口

需要受支持的 Windows、可用的 Cloud Files 平台、本地 NTFS 同步根，以及可读取的独立模拟源目录。`doctor` 只做环境检查，不能代替实际注册与水合验证。首次使用空同步根，不要放在 OneDrive 等已有云同步根之内。

从仓库根运行 `scripts/build.ps1` 构建，使用 `scripts/start-demo.ps1` 启动演示。直接命令接口如下，其中 `$Agent` 指向构建生成的 `CloudDrive.Agent.exe`：

```powershell
& $Agent doctor
& $Agent run --source 'C:\CloudDriveLab\Source' --root 'C:\CloudDriveLab\Root'
```

保持 `run` 进程运行，然后在另一个终端读取文件或打开资源管理器：

```powershell
& $Agent status --root 'C:\CloudDriveLab\Root' --path '工作资料\说明.txt'
Get-Content -LiteralPath 'C:\CloudDriveLab\Root\工作资料\说明.txt'
& $Agent release --root 'C:\CloudDriveLab\Root' --path '工作资料\说明.txt'
```

`--path` 是相对同步根的文件路径，不能包含 `..`、`.`、空路径段、绝对路径或备用数据流。`release` 不递归释放目录。完成演示后先在运行窗口按 Ctrl+C，再注销：

```powershell
& $Agent unregister --root 'C:\CloudDriveLab\Root'
```

原型只允许注销**空同步根**。先自行保存本地修改，再清空本地测试条目；软件不会自动替你删除它们。Windows 的 `CfUnregisterSyncRoot` 会移除未水合占位，因此不能将注销当作保留所有本地文件的操作。模拟源不被修改。将非空本地树安全迁移成独立普通文件属于后续功能。

## 关键 API 与策略

| 行为 | 原生接口 / 策略 |
| --- | --- |
| 持久注册本地同步根 | `CfRegisterSyncRoot`，记录本软件根身份 |
| 连接当前提供程序进程 | `CfConnectSyncRoot`，注册 `FETCH_DATA` 等回调 |
| 元数据占位 | `CfCreatePlaceholders`，包括逻辑大小、文件时间和身份 |
| 完整文件水合 | `CF_HYDRATION_POLICY_FULL` |
| 目录预先完整枚举 | `CF_POPULATION_POLICY_ALWAYS_FULL`，由启动扫描创建整个元数据树 |
| 提交数据或失败状态 | `CfExecute` + `CF_OPERATION_TYPE_TRANSFER_DATA` |
| 查询平台状态 | `CfGetPlaceholderInfo` 等 CFAPI 信息查询 |
| 安全访问占位文件 | `CfOpenFileWithOplock`，独占操作需要拒绝正在使用的文件 |
| 释放本地内容 | `CfDehydratePlaceholder` |
| 停止 / 注销 | `CfDisconnectSyncRoot` / `CfUnregisterSyncRoot` |

`FULL` 与 `ALWAYS_FULL` 水合策略不同：前者支持脱水并在读取时完整水合；后者禁止形成非完整水合占位文件，因此不适合本项目。目录的 `ALWAYS_FULL` 是独立的“填充策略”，表示目录条目由提供程序预先建立，并不要求下载文件正文。

## 水合路径

回调根据持久文件身份定位模拟源，检查请求的路径、大小、时间和文件身份是否仍匹配。源文件以只读方式打开，并限制写入/删除共享，避免水合过程中内容被替换。Host 使用有上限的缓冲区循环读取并提交数据，不能以 `ReadAllBytes` 或等价方式载入整个大文件。

传输操作使用本次回调的连接键、传输键和请求键。取消与异常必须结束对应请求并向平台提交失败，不能让资源管理器无限等待。接口的偏移、长度和文件大小使用 64 位值。

原型启动时只扫描目录元数据。读取正文由实际文件访问触发；文件大小显示为远端逻辑大小，不代表占位已占用同等磁盘空间。集成测试应查询真实 CFAPI 状态及磁盘占用，不能只根据资源管理器图标或文件长度判断。

## 释放路径

释放前必须确认目标是本软件的占位文件，且获得适用的独占文件访问。已固定保留、正在使用、平台标记未同步、源已变化或内容校验不一致的文件应拒绝释放。

本阶段尚无上传功能，即使本地文件能正常编辑，也不能认为修改已经同步。释放需要额外比较本地和模拟源的 SHA-256，不能单凭时间或平台“已同步”标志判定安全，也不能为通过检查而调用“标记已同步”。成功释放保留占位和模拟源内容。

Host 默认拒绝外部脱水请求。`release` 在源文件禁止写入、目标 CFAPI 独占引用仍然保持的情况下，完成比对后临时设置当前用户会话中的单文件事件；Host 的脱水通知只在该事件有效时允许操作。调用完成立即复位，进程退出后句柄自动关闭。这个协调机制用于第一阶段两个本软件进程间的释放操作；后续 Agent IPC 可替换它。

## 当前限制与人工检查

- 尚未实现 Agent 到 Host 的文件数据 IPC、SMB、自动重连或双向同步。
- 源目录被更改后没有自动刷新占位的功能；对版本不一致采取拒绝策略。
- 启动时递归建立全部目录条目，大目录启动开销仍需测量；未实现按需目录枚举。
- Explorer 的状态图标、双击打开和实际关联应用必须人工验收。自动化读取触发真实 `FETCH_DATA`，但不等同于已经检查每一种 Shell 展示行为。
- 杀进程、休眠唤醒、大于 4 GB 文件、并发和磁盘不足等场景应按测试计划继续验证；不能从小文件测试推断全部通过。

## Microsoft 参考

- [CfRegisterSyncRoot（含水合与填充策略说明）](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot)
- [CfConnectSyncRoot](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot)
- [CfCreatePlaceholders](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfcreateplaceholders)
- [CfExecute](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfexecute)
- [CfOpenFileWithOplock](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock)
- [CF_DEHYDRATE_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_dehydrate_flags)
# 第二阶段补充

现已新增索引驱动的批量占位、SMB 管道数据块与范围水合。细节与测试限制见 [PHASE2.md](PHASE2.md) 和 [PHASE2_VALIDATION.md](PHASE2_VALIDATION.md)。以下保留第一阶段设计背景。
