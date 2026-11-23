技术路线：Windows CFAPI + Windows 原生 SMB 客户端 + C#/.NET + C++
项目定位： Windows 原生按需云文件管理工具。
核心目标： 远端文件完整展示在本地，需要时自动通过 SMB 读取，不需要时释放本地文件内容。
开发一款 Windows 桌面软件，实现：
1. 通过 SMB 协议直接连接 NAS 或其他 SMB 文件服务器。
2. 支持自定义服务器 IP、域名、端口、账号和密码。
3. 支持指定 SMB 共享文件夹及其下级目录。
4. 本地显示远端完整目录结构，但不下载实际文件内容。
5. 双击文件时自动从 SMB 服务器下载。
6. 文件下载后在本地缓存，后续访问直接使用。
7. 本地修改文件后自动上传至 SMB 服务器。
8. 支持释放本地文件内容，不删除远端文件。
9. 支持根据访问时间和缓存容量自动清理。
10. 支持历史文件自动归档。
11. 提供完整的图形化管理界面。
重要原则：本项目不是 SMB 映射盘软件，而是基于 SMB 作为远端数据源的原生云文件系统。
```text
Windows 资源管理器
        │
        ▼
本地 CloudDrive 文件目录
        │
        ▼
Windows Cloud Files API
        │
        ▼
CloudProviderHost（C++）
        │
        ├── 文件占位管理
        ├── 文件水合
        ├── 文件释放
        └── 状态更新
        │
        ▼
CloudDrive Agent（C#）
        │
        ├── SMB连接管理
        ├── 文件下载
        ├── 文件上传
        ├── 文件变更检测
        ├── 缓存管理
        └── 自动归档
        │
        ▼
Windows SMB Redirector
        │
        ▼
SMB服务器（NAS）
```
SMB 实现原则：优先使用 Windows 原生 SMB 客户端，不自行实现 SMB 协议，不使用 SMBLibrary 作为首期核心组件。
这样可以直接利用 Windows 自身的 SMB 协议协商、认证、签名和安全机制。
这是本次调整的重点。
用户可以在软件中新增多个 SMB 存储连接。
每个连接独立保存配置。
完整连接示例：
```text
服务器：192.168.1.100
端口：445
共享名称：Public
远端路径：Documents
用户名：admin
密码：********
```
连接目标：
```text
\\192.168.1.100\Public\Documents
```
端口不拼接到 UNC 路径中，而是由 SMB 映射层单独指定。
必须支持用户手动配置端口。
默认值：
```text
445
```
允许用户填写其他有效 TCP 端口。
端口配置要求：
- 默认使用445。
- 允许自定义端口。
- 端口范围为1至65535。
- 输入时校验合法性。
- 修改端口后重新建立连接。
- 不允许将端口配置写死在代码中。
- 连接测试时明确显示实际使用端口。
Windows 11 24H2及以上支持 SMB 客户端连接替代 TCP 端口。
通过 Windows PowerShell 的 New-SmbMapping 实现：
```powershell
New-SmbMapping `
  -LocalPath "X:" `
  -RemotePath "\\192.168.1.100\Public" `
  -TcpPort 1445 `
  -UserName "admin" `
  -Password $securePassword
```
其中：
- X: 为软件内部管理的 SMB 映射盘符。
- RemotePath 为远端共享目录。
- TcpPort 为用户自定义端口。
- UserName 为用户配置的账号。
- Password 为安全处理后的密码。
实际开发时不得将密码直接拼接进外部命令字符串或日志。通过受控的 PowerShell Runspace 调用，并使用安全凭据管理。
注意：
1. 自定义 TCP 端口需要 Windows 11 24H2 或更高版本。
2. 创建替代端口映射需要管理员权限。
3. SMB服务器端必须能够在指定端口接收连接。
4. 如果 NAS 只监听445，单纯修改客户端端口不能建立连接。
5. 如果使用路由器将外部1445转发到NAS内部445，客户端连接外部1445时，必须确保转发和网络策略正确。
Windows 11 24H2 以下版本，首期仅保证标准445端口连接。若需要全面支持旧版 Windows 的任意端口，需要单独评估其他 SMB 协议实现及其安全能力。
必须支持用户自己输入账号和密码。
要求：
- 不依赖当前 Windows 登录账号。
- 不要求用户事先在资源管理器中映射网络磁盘。
- 支持 NAS 独立账号。
- 支持域账号或工作组账号。
- 支持修改密码。
- 支持清除保存的凭据。
- 支持连接失败后重新认证。
密码保存方式：
优先使用 Windows Credential Manager 或 DPAPI。
SQLite 只保存凭据引用，不保存明文密码。
密码在连接时临时读取，连接完成后尽可能释放内存中的敏感数据。
不能把密码写入：
- 日志。
- 配置文件明文。
- 命令行参数。
- 异常堆栈。
- 调试输出。
用户点击测试连接后，依次执行：
1. 检查服务器地址格式。
2. 检查端口合法性。
3. 检查目标 TCP 端口连通性。
4. 建立 SMB 会话。
5. 进行账号密码认证。
6. 检查共享目录是否存在。
7. 检查读取权限。
8. 检查写入权限。
9. 返回连接结果。
界面应区分：
- 网络不可达。
- 端口无法连接。
- SMB协议协商失败。
- 用户名或密码错误。
- 共享目录不存在。
- 只有读取权限。
- 完全连接成功。
测试连接不能通过创建或删除用户文件来判断权限，应采用临时测试目录或其他可安全清理的方式。
每个 SMB 连接在内部建立独立映射。
要求：
- 自动分配可用映射盘符。
- 多个 SMB 连接不能互相冲突。
- 软件退出或异常后能够恢复连接。
- 支持断开连接。
- 支持重新连接。
- 不允许删除其他软件建立的映射。
- 删除连接时只清理本软件创建的映射。
CloudProviderHost 与 SMB 访问进程必须运行在能够访问对应 SMB 映射的用户会话中。
建议使用当前登录用户下的后台 Agent，而不是直接使用 LocalSystem 服务访问用户级映射盘，避免出现盘符在不同登录会话中不可见的问题。
使用 Windows Cloud Files API。
将 SMB 共享目录中的文件结构映射到本地 NTFS 同步根目录。
例如：
远端 SMB：
```text
\\192.168.1.100\Public\Documents
│
├── 工作资料
│   ├── 项目方案.docx
│   ├── 项目报告.pdf
│   └── 设计文件.psd
│
└── 个人资料
    ├── 照片
    └── 视频
```
本地显示：
```text
D:\CloudDrive\我的NAS
│
├── 工作资料
│   ├── 项目方案.docx
│   ├── 项目报告.pdf
│   └── 设计文件.psd
│
└── 个人资料
    ├── 照片
    └── 视频
```
本地文件只保留占位信息，不主动下载实际内容。
用户双击文件：
1. Windows 触发 CFAPI FETCH_DATA。
2. CloudProviderHost 获取文件唯一标识。
3. 向 Agent 发起下载请求。
4. Agent 根据文件映射关系找到 SMB 远端路径。
5. 通过 Windows SMB Redirector 读取远端文件。
6. 将文件数据写入 CFAPI。
7. 完成文件水合。
8. 文件正常打开。
V1 采用完整文件水合策略，先完整下载再完成文件访问。
支持大文件流式读取，不允许一次性将超大文件全部加载进内存。
用户选择释放空间：
1. 检查文件是否已同步。
2. 检查文件是否正在使用。
3. 检查是否存在未上传修改。
4. 调用 CFAPI 释放文件内容。
5. 保留文件占位。
6. 资源管理器重新显示云朵状态。
不能删除 SMB 服务器上的文件。
当用户修改文件时：
1. 监听本地文件变化。
2. 判断文件是否正在写入。
3. 等待文件关闭。
4. 将任务加入上传队列。
5. 计算文件 SHA-256。
6. 检查远端版本。
7. 上传到 SMB 服务器。
8. 校验上传结果。
9. 更新同步状态。
上传时优先采用临时文件写入，再通过 SMB 重命名替换目标文件，避免上传中断造成远端原文件损坏。
远端文件可能被其他设备修改。
采用定时扫描：
- 默认每10分钟。
- 支持用户自定义。
- 比较文件大小。
- 比较修改时间。
- 比较文件标识。
- 必要时计算哈希。
新增文件创建本地占位符。
远端修改更新本地占位元数据。
远端删除同步更新本地状态。
如果本地存在未上传修改，则进入冲突处理。
禁止静默覆盖。
当本地与远端同时发生修改：
- 保留本地版本。
- 保留远端版本。
- 创建冲突副本。
- 记录冲突时间。
- 提醒用户选择处理方式。
支持：
1. 查看缓存总容量。
2. 查看已下载文件。
3. 查看最近访问文件。
4. 手动释放单个文件。
5. 手动释放整个目录。
6. 按缓存容量自动释放。
7. 按访问时间自动释放。
示例：
```text
缓存最大容量：50GB
超过容量：自动清理
清理方式：优先释放最久未访问文件
目标容量：40GB
始终保留文件：不参与清理
```
释放前必须确认文件已经同步至 SMB。
本地修改未上传的文件不能自动释放。
本模块独立于缓存管理。
首期规则：
创建时间超过180天，并且修改时间超过180天。
判断公式：
```text
CreationTime < Now - 180 Days
AND
LastWriteTime < Now - 180 Days
```
两个条件必须同时成立。
归档任务支持：
- 源目录。
- 目标 SMB 存储连接。
- 目标共享目录。
- 文件类型过滤。
- 是否递归。
- 每周执行时间。
- 手动执行。
1. 扫描文件。
2. 筛选符合条件的文件。
3. 检查文件使用状态。
4. 检查同步状态。
5. 上传至归档目标。
6. 计算 SHA-256。
7. 校验远端文件。
8. 创建归档映射记录。
9. 确认成功后更新原位置。
10. 保留原位置的占位文件。
采用先复制、后校验、再替换的安全策略。
任何异常都不能直接删除源文件。
如果 SMB 服务器不能提供可靠的创建时间，跳过该文件并记录原因，不允许用修改时间替代。
```text
Id
Name
ServerAddress
Port
ShareName
RemoteSubPath
Username
CredentialReference
Domain
MappedDriveLetter
ConnectionStatus
LastConnectedAt
CreatedAt
UpdatedAt
```
其中：
- Port 默认445。
- CredentialReference 指向 Windows 安全凭据。
- MappedDriveLetter 记录软件内部创建的映射盘符。
```text
Id
StorageConnectionId
RemoteFileId
RemotePath
LocalPath
FileName
Extension
FileSize
CreationTime
LastWriteTime
LastAccessTime
ContentHash
HydrationState
SyncState
IsPinned
IsDirectory
LastSyncTime
```
```text
Id
FileId
TaskType
TaskStatus
RetryCount
ErrorMessage
CreatedAt
StartedAt
CompletedAt
```
```text
Id
TaskName
SourcePath
TargetStorageId
TargetPath
CreationDays
ModificationDays
ScheduleType
Enabled
LastRunTime
```
```text
Id
ArchiveTaskId
OriginalPath
ArchivedPath
FileSize
SHA256
ArchiveStatus
ArchivedAt
ErrorMessage
```
```text
Id
FileId
LocalVersionHash
RemoteVersionHash
ConflictPath
ResolutionStatus
CreatedAt
ResolvedAt
```
采用 Windows Fluent Design 风格。
显示：
- 已连接的 SMB 存储。
- 远端文件数量。
- 本地缓存容量。
- 占位文件数量。
- 待同步任务。
- 失败任务。
- 最近操作记录。
提供新增 SMB 存储表单：
```text
存储名称：       [我的NAS          ]

服务器地址：     [192.168.1.100    ]

SMB端口：        [445              ]

共享名称：       [Public           ]

远端子目录：     [Documents        ]

用户名：         [admin            ]

密码：           [********         ]

                [测试连接] [保存]
```
界面要求：
- 端口默认445。
- 账号密码可自定义。
- 支持显示或隐藏密码。
- 支持测试连接。
- 支持修改和删除。
- 支持多个存储连接。
- 连接失败显示明确原因。
展示缓存容量、清理规则、释放空间操作。
展示归档任务、筛选条件、执行历史。
展示下载、上传、等待、失败和冲突任务。
包括：
- 开机自启动。
- 自动连接。
- 下载并发数。
- 上传并发数。
- SMB超时时间。
- 自动重试次数。
- 远端扫描间隔。
- 缓存容量。
- 自动释放规则。
1. 禁止启用 SMB1。
2. 优先使用 SMB 3.x。
3. 优先启用 SMB 签名。
4. 支持 SMB 加密，服务器支持时可要求加密。
5. 不允许跳过账号认证。
6. 密码不得明文保存。
7. 不允许静默覆盖文件。
8. 上传失败保留本地文件。
9. 归档失败保留源文件。
10. 断网后自动恢复。
11. 不允许将未同步文件释放。
12. 所有文件操作可追踪。
13. SMB连接超时后自动进入离线状态。
如果需要跨公网访问，优先通过 VPN 建立安全网络，再连接 SMB。不要直接向公网开放 SMB 的 TCP 445 端口。
先使用本地目录模拟远端 SMB。
完成：
- 注册 Sync Root。
- 创建占位文件。
- 云朵图标显示。
- 双击触发 FETCH_DATA。
- 文件水合。
- 文件正常打开。
- 释放本地内容。
完成：
- SMB连接管理。
- 服务器地址配置。
- 自定义端口。
- 用户名密码配置。
- 共享目录选择。
- 连接测试。
- 凭据保存。
- 多连接管理。
- 自动重连。
重点验证：
```text
配置SMB连接
    ↓
连接NAS
    ↓
认证成功
    ↓
扫描共享目录
    ↓
获取文件元数据
    ↓
本地创建占位文件
    ↓
双击文件
    ↓
通过SMB读取
    ↓
CFAPI完成水合
```
完成：
- 本地修改监听。
- 文件上传。
- 远端变化检测。
- 新增、修改、删除和重命名。
- 文件冲突处理。
- 失败恢复。
完成：
- 缓存统计。
- 手动释放。
- 自动释放。
- 容量控制。
- 最近访问记录。
完成：
- 创建归档规则。
- 180天时间筛选。
- 每周执行。
- 归档上传。
- SHA-256校验。
- 原位置占位替换。
- 归档记录。
完成：
- Windows安装程序。
- 开机启动。
- 后台Agent。
- 托盘管理。
- 异常恢复。
- 配置备份。
- 软件卸载。
- 日志导出。
另外重点测试：
- 4GB以上文件。
- 超大文件夹。
- 中文文件名。
- 长路径。
- 特殊字符。
- 多任务并发。
- SMB会话过期。
- 凭据错误及修改。
- NAS重启。
- Windows休眠和唤醒。
- 网络切换。
- 本地磁盘空间不足。
```text
CloudDriveLite/
│
├── src/
│   ├── CloudDrive.UI/
│   ├── CloudDrive.Agent/
│   ├── CloudDrive.Core/
│   ├── CloudDrive.SMB/
│   ├── CloudDrive.Infrastructure/
│   └── CloudProviderHost/
│
├── tests/
│   ├── UnitTests/
│   ├── IntegrationTests/
│   └── CFAPITests/
│
├── docs/
│   ├── ARCHITECTURE.md
│   ├── SMB_CONNECTION.md
│   ├── CFAPI.md
│   ├── DATABASE.md
│   └── TEST_PLAN.md
│
├── installer/
│
└── README.md
```
1. 必须使用 SMB 协议，不使用 WebDAV。
2. 必须支持自定义 SMB 服务器地址。
3. 必须支持自定义 SMB TCP 端口。
4. 必须支持自定义账号和密码。
5. 必须支持多个 SMB 存储连接。
6. 必须支持独立共享目录和子目录。
7. 优先使用 Windows 原生 SMB Redirector。
8. 必须使用 CFAPI 实现文件占位和按需水合。
9. 不允许将 SMB 映射盘直接作为最终用户的云文件目录。
10. 不允许下载全部文件来模拟占位。
11. 不允许使用普通文件复制代替 CFAPI。
12. 必须处理凭据安全、文件冲突和异常恢复。
13. 必须支持网络中断和自动重连。
14. 必须确保文件上传成功后才更新同步状态。
15. 必须确保归档校验成功后才允许替换原文件。
16. 必须保证缓存释放不影响远端原文件。
开发顺序：
第一步只做 CFAPI 本地模拟原型。
第二步打通 SMB 连接，包括自定义端口和账号密码。
第三步完成 SMB 文件读取与 CFAPI 水合。
第四步完成双向同步。
第五步完成缓存和归档。
不要一次性生成全部代码，不要在核心技术未验证前投入大量 UI 开发。
最终软件应该实现：
用户打开 Windows 资源管理器，可以看到所有远端 SMB 文件。
用户不需要提前下载文件，也不需要手动连接 NAS。
双击文件，自动从 SMB 读取并下载。
修改文件，自动上传回 SMB。
不使用的文件，可以自动释放本地空间。
超过设定时间的历史文件，可以自动归档至其他 SMB 存储位置。
最终体验：本地像使用普通文件夹一样使用云端文件，实际文件由 NAS 保存，本地只保留必要的占位和缓存。