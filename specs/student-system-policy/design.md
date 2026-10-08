# 学生机系统策略设计

## 组件边界

`VeyonCampus.Core` 定义严格 JSON 文档、RSA-PSS 签名、策略编译、逐项快照、原值/工具值冲突检测和可移植运行时。TeacherConsole 持有独立系统策略签名私钥，提供六个可编辑开关、学生 SID 输入、软件安装策略审核/执行确认、签名推送、状态和逐台结果。Student SYSTEM Agent 持有 schema v5/v6 配置包中的系统策略公钥，验证校区、purpose、版本、账户 SID 后调用 Windows 适配器。

系统策略长期有效，具有独立 revision 与撤销语义，不复用网站/应用课堂策略的 24 小时签名信封或到期清理。状态库保存在 SYSTEM/Administrators-only 的 ProgramData 目录；每个受管值均记录目标 SID、部署前原值、工具应用值和状态。所有策略先整体做 preflight，再写入 `Pending` 事务；Agent 在启动和定期轮询时先协调未完成事务，再处理新签名命令。

学生身份预检按 SID 展开本机本地组成员图，而不是只查直接组成员。系统策略拒绝目标账户通过任意嵌套本地组进入 Administrators、Account Operators 或 Network Configuration Operators；任一本机组清单无法完整读回、必需内置组缺失或图数据超过边界时均失败关闭，不开始策略写入。此规则与应用策略共享本地组图解析器；枚举依赖 [Get-LocalGroupMember](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.localaccounts/get-localgroupmember)，遇到无 SID 的成员不会静默忽略。Windows 实际组枚举和令牌效果仍需实机确认。

每个学生 HKCU 管理策略键同时作为一个 `user-acl` 事务资源管理。安装后该键所有者为 SYSTEM、组为 Administrators、DACL 受保护，只给 SYSTEM/Administrators 完全控制、目标学生读取；原 Owner/Group/DACL 安全描述符与策略值在同一 Pending 日志中快照、读回和恢复，不修改 SACL。活动版本升级时，Agent 先核对原策略值未变，再把缺少 ACL 资源的旧状态迁移到只读 ACL；外部 DACL 不匹配时保留现场并报告冲突。仅锁定精确策略键，不改写整个用户配置 hive 或其父级 ACL。微软注册表 API 删除子键时会对目标子键请求 `DELETE` 权限；`RegistryRights.ReadKey` 提供读取所需权限，不包含修改或删除权限。[RegDeleteKeyEx](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regdeletekeyexa)、[Registry Key Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-key-security-and-access-rights)

## 策略文档与传输

文档固定 `VeyonCampus.StudentSystemPolicy.v1` purpose、schema 1、校区 ID、严格单调版本、UTC 签发时间、目标 SID 集合和六个布尔值。独立的 `SystemPolicySigningKeyStore` 使用教师当前 Windows 用户密钥存储；学生配置包 schema v5 增加 `systemPolicyPublicKey`，v1–v4 解释保持原样。HTTP 端点为 `/v1/system-policy`，读取状态用教师签名的请求和一次性 nonce；Agent 身份密钥签署状态和策略回执，TeacherConsole 仅在目标公钥已固定且本次请求摘要匹配时报告回执成功，Windows 实际效果仍须现场核验。统一壁纸随 Student Agent 发布资源传递，不放入 CloudBase 校区配置包，因此无需为固定资产单独扩展配置包 schema。

“禁止修改网络设置”同时管理传统 Network Connections 属性/创建/重命名/删除入口，以及用户配置 `SettingsPageVisibility` 中列出的网络、Wi‑Fi、VPN、代理、热点和飞行模式页面；它只隐藏网络配置页面，保留 Settings 应用的其他页面和现有网络连接。Windows Agent 只在 `EditionID` 属于微软列出的 Pro、Enterprise、Education 或 IoT Enterprise 版本时写入页面可见性策略，否则在事务写入前报告不支持；家庭版不会仅凭注册表读回被误报为已限制。页面 ID 依据微软当前 Settings URI 与 Page Visibility 文档；目标 Windows 10/11 的实际页面覆盖仍列入实机矩阵。[Page Visibility policy](https://learn.microsoft.com/en-us/windows/configuration/settings/page-visibility) 支持阻止页面的直接 URI 导航，[Settings Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-settings) 列出适用版本，[Settings URI reference](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings) 列出网络页面，[Network Connections CSP](https://learn.microsoft.com/zh-cn/windows/client-management/mdm/policy-csp-admx-networkconnections) 定义传统网络连接策略值。

Teacher 和 StudentSetup 包含 schema v5 的有效 studentApp/Veyon/System Agent 兼容范围、完整载荷 SHA-256 清单和公钥；schema v6 保留这些内容并增加首次部署建议。Node 配置包 API、ASP.NET 对照 API、OpenAPI 和数据库迁移接受 v3–v6；新对象键版本后缀按 schema 严格匹配。独立 Node 应用发布 API 使用清单 schema v3，同时声明 Application Policy 与 Student System Policy 能力；`/v1`、`/v2`、`/v3` 端点为已安装旧客户端保留各自的规范签名。新 Teacher/Agent 先发行并验证，再向旧 Agent 推送策略；旧端明确返回不支持，不回报成功。共享环境部署、只读读回及 v5/v6 合成写入验收是运行状态，统一见[项目任务主表 P13-05](../../docs/开发路线与任务清单.md)和[日期记录](../../docs/records/2026-10/续作核查记录-20261008.md)。完整签名发行还需生产密钥与发布凭据。

## Windows 策略映射

- 壁纸：统一图像按负责人选定的蓝色背景、偏右四窗徽标样式生成，作为 `VeyonCampus.Agent` 发布资源。Agent 校验代码固定 SHA-256、JPEG 结构、RGB/尺寸/长宽比和路径重解析点，再原子复制到 `%ProgramData%\\VeyonCampus\\SystemPolicy\\Assets\\<sha256>.jpg`。目录由 SYSTEM/Administrators 写入、学生可读取，目标文件不授予学生写入或执行权限；策略指向该内容地址，策略状态中的已安装值记录路径引用。Agent 发布包完整性清单包含该 JPG，程序复制白名单显式允许 `.jpg`。更新和撤销不覆盖或删除旧内容地址资源，因此不会让已有策略引用悬空；当前不做资源垃圾回收。Windows 10/11 画面和裁切效果仍须实机验收。生成图不是 Windows 官方壁纸；徽标公开分发审查继续由 P9-02 管理。Windows 官方桌面背景策略支持本机路径，不同屏幕比例会裁切。[桌面背景配置与裁切](https://learn.microsoft.com/en-us/windows/configuration/background/)、[桌面壁纸策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-desktop)、[Windows 11 Bloom](https://blogs.windows.com/windowsexperience/2021/10/06/windows-11-blossoms-with-bloom-a-new-symbol-for-a-new-operating-system/)
- 时间：用 Windows LSA 用户权利 API 从目标 SID 移除 `SeSystemtimePrivilege` 和 `SeTimeZonePrivilege`，不替换完整用户权利清单；保留 `LOCAL SERVICE`、SYSTEM 和管理员的原分配。若权限由不可安全移除的广泛组提供，则拒绝声称策略生效并报告冲突。
- 网络：对目标用户写入 `Network Connections` 管理模板值，限制 LAN 属性、改动组件、创建新连接及更改连接属性；验证目标不是 Administrators 或 Network Configuration Operators。Agent 不改网卡状态、TCP/IP 当前参数、DNS、WLAN 凭据、防火墙或控制端口。
- 软件安装：教师必须确认后才可推送；目标用户的 Windows Installer per-user 设置和 Appx/Store 安装入口由系统策略事务管理。开启限制时，单一 AppLocker EXE 集合合并长期运行 allowlist、Windows/应用放行基线与课堂规则；审核模式使用已登记程序清单模拟命中，执行模式将课堂拒绝规则合并到同一集合。MSI 和脚本集合不由 AppLocker 接管。写入前递归检查 `%PROGRAMFILES%`、Windows 放行目录及其中的目录/文件 ACL，解析学生直接和本地嵌套组 SID；学生拥有或可写的路径、重解析点、不可读 ACL 或超过两分钟的审查都会失败关闭，不自动修复 ACL。AppLocker 本机/有效策略冲突会阻止写入；规则命中和 Windows 核心程序兼容仍须实机验收，因此不得在实机验证前宣称机房可用。
- 用户账户：验证所有目标为启用的本地标准用户，不允许 Administrators、Account Operators 或网络配置组成员。Windows 原生权限阻止普通用户创建/删除账户和更改本地组成员；对目标本地用户显式设置 `PasswordChangeable=false`，禁止本人改密，同时保留管理员重置密码的权限。
- Control Panel：OFF 不写 `Policies\\Explorer\\NoControlPanel`；ON 写 `REG_DWORD 1`，禁止 Control.exe 和 SystemSettings.exe 启动。恢复遵循逐值拥有权检查。

## Windows 支持生命周期门槛

技术策略能写入注册表或读回成功，不代表该 Windows 版本仍受 Microsoft 支持。部署和验收前必须记录 `EditionID`、`DisplayVersion`、完整 build/UBR、servicing channel、最新累积更新状态及 ESU 状态；不能仅凭 `19044` 判断是普通 Windows 10 21H2 还是 LTSC 2021。普通 Windows 10 Home/Pro/Enterprise/Education 22H2 已于 2025-10-14 结束更新；普通 Windows 10 Enterprise/Education 21H2 更早结束服务。Enterprise LTSC 2021 更新支持至 2027-01-12，IoT Enterprise LTSC 2021 至 2032-01-13。Windows 10 22H2 设备只有在符合组织的 ESU 计划及更新状态时，才可作为仍接收安全更新的目标；应用自身不验证 ESU 授权。

当前 Windows 10 验收快照为 build `19044.3448`，但现有记录无法区分普通 21H2 与 LTSC 2021，且其补丁水平低于 Microsoft 当前 release information 页面列出的 2026-09 build `19044.7727`。在确认真实 servicing channel、产品支持状态并更新到组织批准的补丁水平之前，不把这台快照作为受支持 Windows 10 兼容证据，也不扩大到机房部署。该部署门槛不等同于 Agent 的运行时强制检查；Windows 版本兼容和 AppLocker 实际效果仍须按 P13-01/P13-08 在隔离设备验收。

## 冲突、事务与恢复

系统策略按 SID 的 profile list 查找本地 ProfileImagePath。校验路径位于本机卷、无重解析点、配置文件有效；已加载配置直接操作，未加载时在唯一临时 HKU 名称下加载/卸载并保证 finally 清理。不存在 profile 的 SID 不允许部分部署。

写前记录原值和期望值并保存 durable Pending；写后逐个读回。重启后每项只接受原值或工具期望值，其他值表示外部冲突，保留当前 Windows 状态。撤销或移除某个 SID 时仅当当前值仍等于工具写入值才恢复原值；并发/部分失败保留有界事务记录，逐项返回状态。系统策略不设自动过期；学生 Agent 卸载的明确确认流程先撤销所有本工具拥有的系统值，再移除 Agent。

HKCU 管理策略值处于学生自己的配置单元，单靠签名或 UI 隐藏不能阻止学生直接修改值。Agent 对写入值所在的精确子键设置只读学生 ACL，状态库记录部署前 Owner/Group/DACL SDDL 和工具 ACL 标记；先写策略值再收紧 ACL，撤销时先恢复值再恢复原 ACL。只管理明确列入 allowlist 的策略子键，不修改父级权限或 SACL。若原来不存在的策略键撤销时出现非空内容，Agent 保留该内容并使事务待复核；任何写入、ACL 读回或撤销失败均保留 Pending，不能汇报策略完成。

更新器先查询受保护的应用策略与系统策略活动状态。Student/Teacher 发布签名清单声明功能能力版本；仍有应用策略状态时要求 application-policy capability >=1，仍有系统策略状态时要求 system-policy capability >=1。桌面检查、Teacher 推送、StudentSetup 离线安装和学生 Agent 命令均在安装/替换前应用对应兼容门槛。候选版本验证全部签名、角色、架构、版本、五/六文件摘要和兼容范围后才切换；新 Agent 启动并健康读回后再清理旧文件；失败恢复旧 Agent 并保留状态库和防重放高水位。

## 验证

2026-10-08 本机 .NET 检查为 53/53，包含 `user-acl` 资源编译、旧活动策略迁移、ACL 冲突及撤销状态检查。该套件在 macOS 上运行，只验证可移植事务逻辑；尚未运行 Windows PowerShell 注册表 ACL 操作。Node/API、角色构建、线上体验环境、发布许可与密钥的最新证据集中在[项目概览](../../docs/README.md)、[项目任务主表](../../docs/开发路线与任务清单.md)和[当日实施记录](../../docs/records/2026-10/续作核查记录-20261008.md)。Windows/浏览器/手机实机边界仍由项目任务主表跟踪。

## Microsoft 依据

- [AppLocker 系统要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker)
- [AppLocker 规则与默认隐式拒绝](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/working-with-applocker-rules)
- [防止修改桌面背景与 Desktop Wallpaper 策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-desktop)
- [桌面背景配置、Windows 版本支持与屏幕裁切](https://learn.microsoft.com/en-us/windows/configuration/background/)
- [Windows Theme 文件与主题包图像资源](https://learn.microsoft.com/en-us/windows/win32/controls/themesfileformat-overview)
- [Windows 用户权利与时间/时区权限](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-userrights)
- [网络连接 ADMX 策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-networkconnections)
- [应用包安装策略](https://learn.microsoft.com/windows/client-management/mdm/policy-csp-applicationmanagement)
- [Windows Installer per-user 与安装限制](https://learn.microsoft.com/en-us/windows/win32/msi/disableuserinstalls)
- [Control Panel/Settings 限制](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-controlpanel)
- [当前支持的 Windows 客户端版本](https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client)
- [Windows 10 发布信息与 servicing channel 生命周期](https://learn.microsoft.com/en-us/windows/release-health/release-information)
- [Windows 10 Enterprise/Education 生命周期](https://learn.microsoft.com/en-us/lifecycle/products/windows-10-enterprise-and-education)
