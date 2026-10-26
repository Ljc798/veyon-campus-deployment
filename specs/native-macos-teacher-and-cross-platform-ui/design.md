# 原生 Mac 教师端与跨端课堂体验：设计

## 设计决策

- 教师界面和校园服务运行在 macOS 原生 Avalonia TeacherConsole 进程中，首个目标为 Apple Silicon `osx-arm64`。
- Mac 本机负责课堂会话、座位资料、学生 Agent 状态、策略签名与下发、手机 HTTPS 服务和屏幕预览生命周期。
- Veyon 当前官方支持 Windows/Linux，macOS 未列为受支持桌面平台；Veyon 官方 Ubuntu PPA 提供 ARM64 包。Mac TeacherConsole 通过同机 Ubuntu ARM64 辅助环境调用经过固定版本校验的 `veyon-cli`，处理 Veyon 目录同步和单帧截图。辅助环境只作本机后台服务，教师不需要登录或操作 Linux 桌面，也不连接 Windows RDP。
- 该 Veyon 路径必须先通过 Apple Silicon 上的连通性、认证、目录导入和单帧截图可行性验证。若任一项无法可靠实现，设计回退为学生 Agent/Companion 提供受限的签名截图能力，并保持 Mac 教师端及手机 API 位于本机。
- Windows TeacherConsole 继续使用当前本机 Veyon CLI。两平台共用课堂业务、签名策略、移动 API 契约和座位布局；系统能力通过平台服务接口隔离。

## 架构

```text
已配对手机
    │ HTTPS / 配对令牌 / 当前课堂座位 ID
    ▼
Mac 原生 TeacherConsole
    ├── Kestrel：配对、课堂、状态、策略、巡视 API
    ├── 校区/机房/座位服务
    ├── macOS Keychain：校区签名密钥、手机 TLS 根密钥、Agent 身份固定
    ├── Student Agent signed HTTP transport：状态读取与策略下发
    └── ITeacherVeyonControlPlane
           └── 本机 Ubuntu ARM64 Veyon helper：白名单 CLI 请求、认证截图

Windows TeacherConsole ── ITeacherVeyonControlPlane ── 本机 Windows Veyon CLI
```

### 平台服务接口

以小型接口替换散落的 `OperatingSystem.IsWindows()` 分支；避免重写 `TeacherViewModel` 或策略协议。

- `ITeacherSecretStore`：按校区保存并读取策略签名私钥、TLS 根密钥和固定身份资料；Windows 使用现有证书/CNG 实现，macOS 使用登录钥匙串。
- `ITeacherNetworkDirectory`：读取、验证和同步地点/电脑名单。Windows 可继续从本机 Veyon 目录读取；Mac 从校园/座位资料生成有身份校验的目录快照，并通过 Veyon helper 导入/读取 Veyon 网络对象。
- `ITeacherVeyonControlPlane`：只公开解析/更新 Veyon 地点、检查 CLI/认证、启动单帧 Screenshot 等受限操作。接口不接受任意命令行或任意文件路径。
- `ITeacherMobileHost` 与 `IMobileTlsIdentityProvider`：保留当前 HTTPS API 契约，在各平台绑定校园私网地址并生成短期服务器证书；Mac 不依赖注册表、Windows 证书存储或 Windows 防火墙 API。
- `ITeacherAutostartStore`：Windows 保留当前开机启动实现；macOS 通过当前用户 LaunchAgent 管理教师端登录启动，不创建 root 服务。

### Mac Veyon helper

- Ubuntu ARM64 helper 与 Mac 教师端通过只绑定 loopback 的短期本地通道通信；每次启动生成随机会话密钥，消息带 nonce、版本和有限长度。
- helper 仅接受枚举型动作：读取地点、读取目标、同步地点、采集指定目标的单帧截图。所有目标先由教师端对照活动课堂目录校验。
- helper 使用固定版本的 `veyon-cli`、固定参数模板和固定工作目录；不暴露 shell、任意命令、外部网络 API 或教师端口。
- 辅助环境内的 Veyon 临时输出落在专用私有目录；每次操作只读取唯一新建的匹配截图，限制最大输入尺寸，处理后删除；取消/超时也执行精确清理。Mac TeacherConsole 收到图像字节后再缩放编码为受限 JPEG。
- 初期 VM 网络使用只允许向校园 LAN 发起连接的 NAT/转发配置。截图和 Veyon 访问不要求公网入口；LAN 可达性、名称解析和 TCP 端口必须在现场验证。
- helper 与 Mac GUI 分离进程；Helper 未启动或健康检查失败时，界面显示“Veyon 服务不可用”，但不伪装手机策略和 Agent 状态也不可用。

### 学生目录与策略

- `CampusDirectoryStore` 中的校区、机房、电脑命名和座位绑定是教师业务目录。Veyon 目录是面向 Veyon 的同步视图；Mac 上不得再把“必须能运行 Windows CLI”作为显示/编辑课堂座位的条件。
- 手机仅提交当前活动课堂返回的目标 ID；Mac 服务将 ID 解析为当前目录中唯一的电脑地址，并在请求前后校验座位、姓名和绑定版本。
- Website、Application、System 等既有策略 DTO、签名算法、预期版本和 Agent 回执规则保持不变。策略私钥通过平台密钥库取用；策略请求继续直连学生 Agent，不经过手机、Veyon helper 或云端转发。
- **已有学生的教师签名密钥需要一次性迁移。** Windows 当前密钥由不可导出的 CNG 密钥保护，不能安全复制到 Mac。Mac 生成新的 Keychain 密钥后，必须先用旧 Windows 教师端签名授权学生 Agent 更新其固定公钥（或通过同一受信任更新通道部署含新公钥的学生包），再由 Mac 接管日常签名；在此之前 Mac 状态读取和策略下发必须返回逐台未信任结果。此迁移只需在切换教师机时进行一次，不要求之后保留 Windows RDP。
- 手机状态优先读取已签名的轻量摘要；不支持或不完整时回退到已签名完整状态。身份异常或状态未知须逐台显示，未知设备不得进入可确认的策略发送集合。
- 课堂地点/座位编辑采用草稿与提交：编辑时活动课堂仍引用当前已确认绑定快照；校验并确认后原子更新课堂目标，旧绑定的巡视租约被撤销。保存失败不改变当前活动目标。

### 手机屏幕巡视

- 手机只请求当前课堂返回的目标 ID；TeacherConsole 在目录和座位绑定中解析目标。helper 返回单帧图片，TeacherConsole 负责缩放、缓存、no-store 响应和逐目标限流。
- 保留现有租约、可见卡片调度、每目标间隔、并发与缓存预算、二维码配对 HTTPS、撤销配对清理和审计脱敏规则。
- 画面卡片按座位排序；手机 390px 窄屏使用双列 16:9 缩略图网格，显示座位、学生姓名、更新时间和读取状态。点开卡片进入单台大图；固定操作栏提供开始/暂停/停止、画质和刷新间隔。
- 停止、离开“画面”页、手机进入后台、课堂/座位改变、服务停止或撤销配对后，前端取消请求并释放 Object URL，服务端撤销租约并清理内存 JPEG。
- 页面文案称“屏幕缩略图/间隔更新”，不称视频流；不提供录像、鼠标键盘控制或云端保存。

## 界面设计规格

### 美学方向

延续项目“教学现场工业控制台”：信息密度适中、分区明确、状态优先、低频说明折叠。主要操作使用稳定的大按钮；异常与未知状态清晰呈现，不通过动画和鲜艳背景制造紧急感。

### 色彩与字体

复用 `docs/ui_rule.md` 与 `Presentation/Theme/Colors.axaml` 的既有 tokens，不引入第二套视觉色板：

- 页面底色 `#F5F7FA`、白色卡片 `#FFFFFF`、次级容器 `#F8FAFC`。
- 深色侧栏 `#112C3D`，选中项 `#19665F`，主操作蓝 `#1769C2`。
- 主文字 `#172D3E`，辅助文字 `#617487`，边框 `#E1E8EE`。
- 状态色沿用成功 `#14805D`、关注 `#AB610E`、失败 `#C13434`、未知 `#64748B`；每种状态同时显示文字或图标。
- Windows 优先 Segoe UI；macOS 使用系统字体，中文回退到系统中文字体；不下载或打包外部字体。
- 正文维持 14px 左右，说明 12–13px，关键状态和错误文字不小于 12px；移动触控操作目标不小于 44×44 CSS px。

### 桌面页面

- TeacherConsole 保留约 224px 深色侧栏、统一页头、当前机房/课堂上下文和浅色工作区；主页面沿用现有六个页面分区与课堂状态。
- 座位页突出地点、座位图和学生关联；持续课堂时编辑，页内明确显示“修改待应用/已同步”的状态；Veyon helper 健康状态独立显示。
- 策略页先选目标和预设，再展示签名状态、版本、逐台结果和恢复入口；危险操作保留确认语义。
- 设备健康页用 Flowery 状态和统计组件帮助扫读；状态文本与既有业务枚举一致。
- StudentSetup 继续是分步骤部署向导：环境准备、校区/座位、配置包和安装结果；SukiUI 负责表单、说明条、对话框和主题一致性。
- SukiUI 作为基础控件与主题层；Flowery.NET 组件按展示用途局部引入，不在全局应用级叠加 DaisyUITheme，不覆盖当前项目 tokens。

### SukiUI 与 Flowery.NET 组件分工

- SukiUI：主题、表单输入、InfoBar、对话框、菜单/导航和可访问焦点交互。
- Flowery.NET：`DaisyStat` 展示统计、`DaisyStatusIndicator` 配合文字展示设备状态、`DaisyAlert` 展示非阻塞反馈、`DaisyProgress` 展示有阶段定义的进度、`DaisyNumberFlow` 展示课堂倒计时；这些组件采用局部主题资源并适配现有语义色。
- 不将 Flowery 控件用于需要虚拟化的 70 台长列表，不为动效牺牲低配电脑滚动和状态更新，也不把 Flowery.NET 控件嵌入 Web PWA。

### 手机页面

- Web PWA 使用 HTML/CSS 和现有品牌 tokens；4 个底部入口为“课堂、限制、画面、设置”，底栏避开 iOS 安全区并保持触控高度。
- 课堂页优先显示当前教室、在线/待核验数量和两到三个主要课堂动作；详细状态按学生卡片展开。
- 限制页采用“选择范围 → 读取状态 → 选择策略 → 确认 → 逐台结果”单列流程。状态未读时显示刷新按钮和具体原因；未知设备不能被误显示为已解除。
- 画面页默认停止采集；开始后懒加载可见卡片，单台大图可返回网格；页面后台时自动暂停。
- 390px 宽度下不依赖横向滚动；兼顾长姓名、长地点名、离线、未知、部分成功、连接失败和大字体。

## 实施阶段

1. **Mac Veyon 可行性门**：在 Apple Silicon Ubuntu ARM64 辅助环境验证 Veyon 固定版本、局域网访问、密钥认证、目录读写和 `Screenshot`；验证失败时记录具体平台限制并实现 Agent/Companion 采集回退。
2. **跨平台宿主基础**：抽取密钥、目录、TLS/HTTP host、启动方式和 Veyon 操作接口；建立 `osx-arm64` TeacherConsole 构建，StudentSetup 继续 `win-x64`。
3. **Mac 教师数据面**：接入本地校区/座位目录、签名状态读取、网站/应用/系统策略和手机配对服务；验证手机连接的是 Mac 教师机。
4. **Mac Veyon 与手机巡视**：接入受限 helper API，打通座位列表、画面缩略图、取消和资源生命周期。
5. **视觉改进**：按既有 UI 阶段迁移 StudentSetup、TeacherConsole 和移动 PWA，逐页引入 SukiUI/Flowery 组件并提供运行截图。
6. **端到端验收**：在 Apple Silicon Mac、Windows 学生机、Android/iOS 手机和真实校园 LAN 验证登录、策略、座位同步、画面、前后台切换与恢复。

## 主要风险与待验证项

- Ubuntu ARM64 虚拟机的 NAT/转发是否能访问校园 Veyon 服务及完成认证；Veyon CLI Screenshot 是否无图形桌面也可运行。
- macOS Keychain 的 RSA 密钥导入、持久化、访问确认和应用重启行为；证书在 Kestrel 与手机信任链中的兼容性。
- 现有学生包固定了 Windows CNG 教师公钥，Mac Keychain 密钥需要通过旧教师签名完成一次性信任迁移；迁移协议和学生 Agent 兼容升级尚未实现。
- 现有 `TeacherViewModel`、房间目录、TLS 身份和教师端启动代码中分布有多处 Windows API。需按平台服务边界渐进迁移，不以移除 guard 代替真实跨平台实现。
- 当前 Mac 为 Apple Silicon；.NET 10.0.401 SDK 已安装在 `/Users/alex/.dotnet/dotnet`，但未加入 `PATH`。Docker Desktop 可运行 Ubuntu 24.04 ARM64 容器；容器内 `apt update` 与宿主机访问 Ubuntu/Launchpad 软件源因 DNS 失败，暂时无法安装或验证 Veyon CLI。当前环境也未安装本机 Veyon。
- GitHub 默认 DNS 解析失败；本次通过 Cloudflare DNS 查到的 GitHub HTTPS 地址临时完成了只读检查和 `pull`。后续远端推送若仍遇到默认 DNS 失败，可用 `http.curloptResolve` 临时指定已验证地址，不要把该地址写入仓库配置。
- SukiUI 7.0.1 与 Flowery.NET 3.6.1 的实际 API、局部主题样式和 macOS 渲染需要以项目锁定版本验证；在线文档只用于发现组件，不替代固定版本编译。
