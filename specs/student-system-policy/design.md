# 学生机系统策略设计

## 组件边界

`VeyonCampus.Core` 定义严格 JSON 文档、RSA-PSS 签名、策略编译、逐项快照、原值/工具值冲突检测和可移植运行时。TeacherConsole 持有独立系统策略签名私钥，提供六个可编辑开关、学生 SID 输入、软件安装策略审核/执行确认、签名推送、状态和逐台结果。Student SYSTEM Agent 持有 schema v5 配置包中的系统策略公钥，验证校区、purpose、版本、账户 SID 后调用 Windows 适配器。

系统策略长期有效，具有独立 revision 与撤销语义，不复用网站/应用课堂策略的 24 小时签名信封或到期清理。状态库保存在 SYSTEM/Administrators-only 的 ProgramData 目录；每个受管值均记录目标 SID、部署前原值、工具应用值和状态。所有策略先整体做 preflight，再写入 `Pending` 事务；Agent 在启动和定期轮询时先协调未完成事务，再处理新签名命令。

## 策略文档与传输

文档固定 `VeyonCampus.StudentSystemPolicy.v1` purpose、schema 1、校区 ID、严格单调版本、UTC 签发时间、目标 SID 集合和六个布尔值。独立的 `SystemPolicySigningKeyStore` 使用教师当前 Windows 用户密钥存储；学生配置包 schema v5 增加 `systemPolicyPublicKey`，v1–v4 解释保持原样。HTTP 端点为 `/v1/system-policy`，读取状态用教师签名的请求和一次性 nonce；未签名学生响应始终标为待核对。

“禁止修改网络设置”同时管理传统 Network Connections 属性/创建/重命名/删除入口，以及用户配置 `SettingsPageVisibility` 中列出的网络、Wi‑Fi、VPN、代理、热点和飞行模式页面；它只隐藏网络配置页面，保留 Settings 应用的其他页面和现有网络连接。Windows Agent 只在 `EditionID` 属于微软列出的 Pro、Enterprise、Education 或 IoT Enterprise 版本时写入页面可见性策略，否则在事务写入前报告不支持；家庭版不会仅凭注册表读回被误报为已限制。页面 ID 依据微软当前 Settings URI 与 Page Visibility 文档；目标 Windows 10/11 的实际页面覆盖仍列入实机矩阵。[Page Visibility policy](https://learn.microsoft.com/en-us/windows/configuration/settings/page-visibility) 支持阻止页面的直接 URI 导航，[Settings Policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-settings) 列出适用版本，[Settings URI reference](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings) 列出网络页面，[Network Connections CSP](https://learn.microsoft.com/zh-cn/windows/client-management/mdm/policy-csp-admx-networkconnections) 定义传统网络连接策略值。

Teacher 和 StudentSetup 包含完全相同的 schema v5 语义：有效 studentApp/Veyon/System Agent 兼容范围、完整载荷 SHA-256 清单和公钥。Node 配置包 API、ASP.NET 对照 API、OpenAPI 和数据库约束源码接受配置包 v1–v5；新对象键版本后缀按 schema 严格匹配。独立 Node 应用发布 API 使用清单 schema v3，同时声明 Application Policy 与 Student System Policy 能力；`/v1`、`/v2`、`/v3` 端点为已安装旧客户端保留各自的规范签名。新 Teacher/Agent 先发行并验证，再向旧 Agent 推送策略；旧端明确返回不支持，不回报成功。应用发布 v3 迁移与函数代码尚未部署到 CloudBase。

## Windows 策略映射

- 壁纸：使用系统内置 `%WINDIR%\\Web\\Wallpaper\\Windows\\img0.jpg` 蓝色 Windows 徽标壁纸；部署前拒绝重解析点并解析 JPEG 结构，要求 RGB 且分辨率至少 1280×720，然后对目标用户写入 `Policies\\System\\Wallpaper`、`WallpaperStyle=10` 与 `Policies\\ActiveDesktop\\NoChangingWallPaper=1`。策略冲突时拒绝写入；代码不识别图片的具体颜色或徽标位置，实际画面仍须在 Windows 上核验；卸载按快照还原。
- 时间：用 Windows LSA 用户权利 API 从目标 SID 移除 `SeSystemtimePrivilege` 和 `SeTimeZonePrivilege`，不替换完整用户权利清单；保留 `LOCAL SERVICE`、SYSTEM 和管理员的原分配。若权限由不可安全移除的广泛组提供，则拒绝声称策略生效并报告冲突。
- 网络：对目标用户写入 `Network Connections` 管理模板值，限制 LAN 属性、改动组件、创建新连接及更改连接属性；验证目标不是 Administrators 或 Network Configuration Operators。Agent 不改网卡状态、TCP/IP 当前参数、DNS、WLAN 凭据、防火墙或控制端口。
- 软件安装：教师必须确认后才可推送；目标用户的 Windows Installer per-user 设置和 Appx/Store 安装入口由系统策略事务管理。开启限制时，单一 AppLocker EXE 集合合并长期运行 allowlist、Windows/应用放行基线与课堂规则；审核模式使用已登记程序清单模拟命中，执行模式将课堂拒绝规则合并到同一集合。MSI 和脚本集合不由 AppLocker 接管。AppLocker 本机/有效策略冲突会阻止写入；规则命中和 Windows 核心程序兼容仍须实机验收，因此不得在实机验证前宣称机房可用。
- 用户账户：验证所有目标为启用的本地标准用户，不允许 Administrators、Account Operators 或网络配置组成员。Windows 原生权限阻止普通用户创建/删除账户和更改本地组成员；对目标本地用户显式设置 `PasswordChangeable=false`，禁止本人改密，同时保留管理员重置密码的权限。
- Control Panel：OFF 不写 `Policies\\Explorer\\NoControlPanel`；ON 写 `REG_DWORD 1`，禁止 Control.exe 和 SystemSettings.exe 启动。恢复遵循逐值拥有权检查。

## 冲突、事务与恢复

系统策略按 SID 的 profile list 查找本地 ProfileImagePath。校验路径位于本机卷、无重解析点、配置文件有效；已加载配置直接操作，未加载时在唯一临时 HKU 名称下加载/卸载并保证 finally 清理。不存在 profile 的 SID 不允许部分部署。

写前记录原值和期望值并保存 durable Pending；写后逐个读回。重启后每项只接受原值或工具期望值，其他值表示外部冲突，保留当前 Windows 状态。撤销或移除某个 SID 时仅当当前值仍等于工具写入值才恢复原值；并发/部分失败保留有界事务记录，逐项返回状态。系统策略不设自动过期；学生 Agent 卸载的明确确认流程先撤销所有本工具拥有的系统值，再移除 Agent。

更新器先查询受保护的应用策略与系统策略活动状态。Student/Teacher 发布签名清单声明功能能力版本；仍有应用策略状态时要求 application-policy capability >=1，仍有系统策略状态时要求 system-policy capability >=1。桌面检查、Teacher 推送、StudentSetup 离线安装和学生 Agent 命令均在安装/替换前应用对应兼容门槛。候选版本验证全部签名、角色、架构、版本、五/六文件摘要和兼容范围后才切换；新 Agent 启动并健康读回后再清理旧文件；失败恢复旧 Agent 并保留状态库和防重放高水位。

## 验证

可移植检查覆盖 schema/签名用途隔离、重复/未知 JSON 字段、版本/校区/SID 验证、六项默认值、profile hive 路径、受保护状态存储、事务崩溃恢复、外部冲突、单 SID 移除、漫游/缺失 profile 拒绝、AppLocker 规则组合与软件安装策略审核门、JPEG 结构/尺寸及 v5 包兼容。Node/ASP.NET/OpenAPI/SQL 契约检查覆盖 v1–v5 包键、release manifest v1–v3 与请求 RPC schema；新 v3 release 迁移和函数代码尚未部署到真实 CloudBase PostgreSQL/HTTP 环境。TeacherConsole/StudentSetup/Agent 构建和 release manifest capability 回归必须通过。

尚待完成/实测：将 release v3 数据库迁移、CloudBase HTTP 函数和 OPA 权限更新到经批准的隔离环境并执行合成发布验证；在可还原 Windows 实机验证 LSA 权限读回、离线用户 hive、安全组、AppLocker 规则命中与核心程序兼容、Wallpaper/Network/Control Panel 生效、MSI/Store/App Installer 行为、更新/回滚、域和 MDM 冲突、注销/重启/卸载恢复。代码结构校验不确认 `img0.jpg` 的具体像素内容或徽标位置。局域网 Agent 应答不替代这些实测。

## Microsoft 依据

- [AppLocker 系统要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker)
- [AppLocker 规则与默认隐式拒绝](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/working-with-applocker-rules)
- [防止修改桌面背景与 Desktop Wallpaper 策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-desktop)
- [Windows 用户权利与时间/时区权限](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-userrights)
- [网络连接 ADMX 策略](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-networkconnections)
- [应用包安装策略](https://learn.microsoft.com/windows/client-management/mdm/policy-csp-applicationmanagement)
- [Windows Installer per-user 与安装限制](https://learn.microsoft.com/en-us/windows/win32/msi/disableuserinstalls)
- [Control Panel/Settings 限制](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-controlpanel)
- [当前支持的 Windows 客户端版本](https://learn.microsoft.com/en-us/windows/release-health/supported-versions-windows-client)
