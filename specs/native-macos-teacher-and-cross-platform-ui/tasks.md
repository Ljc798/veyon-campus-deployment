# 原生 Mac 教师端与跨端课堂体验：实施任务

## 阶段 0：目标环境可行性

- [x] 0.1 确认 Apple Silicon 架构、macOS 版本、.NET 10 SDK 和可用虚拟化运行时（SDK 位于 `/Users/alex/.dotnet/dotnet`，Docker Desktop 可运行 ARM64 容器）。
- [ ] 0.2 建立可恢复的 Ubuntu ARM64 测试环境；验证 Veyon 固定版本 CLI、Key authentication、LAN DNS/端口和网络对象目录读写。正式 ARM64 镜像已从上游 4.11.4 源码构建，CLI 版本为 `4.11.4`；已安装 Ubuntu ARM64 的 QCA RSA 插件，容器可启动，`authkeys list` 和 `authkeys help import` 可运行，导入语法为 `import <KEY> [<FILE>]`。本轮重新运行启动脚本修复了已有容器缺少显式认证模式的问题，并核实 `Authentication/Method=1`、截图与密钥目录路径；容器为非特权 bridge 模式、无发布端口，密钥目录为 `rw,nosuid,nodev,noexec,size=96m` tmpfs。`authkeys list` 目前为空，尚无校区私钥；校园 LAN、真实密钥认证和目录读写仍待验证。
- [ ] 0.3 在无 Veyon 图形窗口的情况下采集一台 Windows 学生机 Screenshot；确认临时图片精确定位、读取、清理、超时和取消。本机 Veyon 4.11.4 CLI 帮助已确认调用顺序为 `feature start <HOST ADDRESS> <FEATURE>`，与 helper 脚本一致。2026-10-27 使用仅容器互通的 Docker 内部网络、临时容器 tmpfs 内生成的测试密钥和 Veyon 4.11.4 HeadlessVncServer 完成本地合成链路复现：发现 DISPLAY 未设置时 CLI 以 QCoreApplication 启动，截图功能因 Qt GUI pixmap 崩溃并产生 0 字节文件；helper 现在为 CLI 子进程指定 `DISPLAY=:99` 和 `QT_QPA_PLATFORM=offscreen`，同一采集脚本返回有效 640×480 PNG，且采集目录没有遗留原图。该结果验证了 Linux helper 到 Veyon Server 的合成截图链路；真实 Windows 学生桌面、校区密钥、校园 LAN、超时与取消仍待验证。
- [x] 0.4 选定 Veyon helper 为首选截图路径；若真实 LAN、密钥认证或截图链路验证失败，再实现 Student Agent/Companion 回退。当前还没有真实学生机截图证据。

## 阶段 1：跨平台教师主机

- [ ] 1.1 增加教师平台服务接口：安全密钥库、网络目录、Veyon 控制面、手机 HTTPS host、TLS 身份和登录启动偏好。
- [ ] 1.2 保留 Windows 实现为默认基线；把当前 Windows API/注册表/CNG/HttpSys 调用放入 Windows 实现中。
- [ ] 1.3 为 macOS 增加 Keychain 密钥/证书管理及 `ApplicationData` 路径，不把策略私钥复制到普通明文文件。
- [ ] 1.4 为 Mac 增加 Kestrel 私网 HTTPS 服务和当前用户登录启动偏好；只绑定私有 LAN 地址，不自动开放公网或更改防火墙。Mac 现在只从活动以太网/Wi-Fi 接口选取 RFC1918 地址用于手机服务与证书 SAN，避免把隧道等非 LAN 接口作为连接地址；Mac 首次设置页已接入 macOS 13+ `SMAppService.mainApp` 开关和批准状态。签名包注册、实际接口枚举、私网 host 真机验收待完成。
- [ ] 1.5 为 TeacherConsole 增加 `osx-arm64` 发布目标和 `.app` 启动入口；StudentSetup 保持 `win-x64`。
- [ ] 1.6 启动时显示真实平台能力清单；无法执行的 Windows 本机安装/修复功能禁用并说明原因。Mac 首次设置页已用 Suki `InfoBar` 与 Flowery 状态点说明本机课堂、学生信任迁移、截图前置条件及 Windows-only 操作；实际运行时能力读回和全部禁用入口核验待完成。

## 阶段 2：Mac 课堂、目录与策略

- [ ] 2.0 完成教师策略公钥迁移：优先支持 Mac 基于原学生包生成更新配置并让学生端安全切换信任根；迁移未完成的学生必须逐台显示未信任，不能假报状态或策略成功。
- [x] 2.0a 增加 Mac 首次设置入口：读取原包清单、保留 Veyon 公钥与电脑前缀，使用 Mac 本机策略公钥生成更新包；旧学生包或公钥校验失败时停止。
- [ ] 2.0b 在真实 Windows 学生机更新 StudentSetup 并重新应用该包；逐台读回 Mac 新签名状态，确认旧策略恢复、手机策略发送和身份校验均正确。
- [ ] 2.1 以 `CampusDirectoryStore` 和座位表提供 Mac 课堂目录；Veyon 目录由平台适配器同步。
- [ ] 2.2 让同一组活动课堂、座位身份和策略服务可运行在 Mac；不把 Windows Veyon CLI 作为课堂页面可见性的前置条件。
- [x] 2.2a 移除教师桌面上对 HTTP Agent 状态、网站/应用/系统策略、手机策略预设和课堂状态同步的 Windows-only 入口限制；TeacherConsole Release 编译通过，Mac/学生机运行验证仍待完成。
- [ ] 2.3 在 Mac 上读取签名 Agent 状态；对不支持轻量摘要的 Agent 回退完整签名状态，保留身份固定和逐台未知状态。TeacherConsole 已开放 Mac 自动轻量摘要轮询、完整设备状态读取及固定条件的签名健康修复；轻量摘要返回不支持时会自动回退完整签名状态。真实学生机和钥匙串运行验证仍待完成。
- [ ] 2.4 在 Mac 上签发和发送现有网站、应用、长期系统策略；保留预期版本比较、教师确认、审计和 Agent 签名校验。
- [ ] 2.5 验证手机配对、撤销、状态读回和网站/应用/系统策略的逐台成功/离线/冲突/未知结果。手机状态读取现在仅在轻量摘要接口明确返回“不支持”时回退完整状态；超时和临时错误不重复请求。手机主策略接口的错误消息映射已覆盖 `/api/policy`；密钥不可用会返回明确原因。真机逐台状态与策略验收待完成。

## 阶段 3：地点和座位编辑

- [x] 3.1 允许课堂持续时打开座位图编辑；当前代码的 `CanEditSeatMap`、`CanToggleSeatMapEditing` 未以活动课堂作为禁用条件。
- [x] 3.2 在地点页提供当前地点资料编辑器；课堂持续时保存前明确确认，再由现有课堂范围协调流程同步。
- [ ] 3.3 确认提交时验证新地点和学生绑定、原子切换课堂目标、刷新手机状态，并撤销基于旧座位的巡视租约。活动课堂座位图保存后会立即重读手机端画面绑定；绑定变化时主动撤销巡视租约并清除缓存画面，未变化时保留租约。手机打开全班巡视时现在会校验启动前后的课堂 ID 与完整座位快照，若期间发生变化则关闭新租约并停止显示旧快照。地点切换并发、写入失败与真机刷新验收待完成。
- [ ] 3.4 验证保存失败、重复目标、课堂切换和策略执行并发时旧目标保持可用且不会混淆学生身份。

## 阶段 4：Veyon helper 与屏幕缩略图

- [x] 4.1 定义仅执行单目标 Screenshot 的本机调用：TeacherConsole 先校验活动课堂绑定，再通过固定 Docker `exec` 参数调用固定脚本；不接收任意命令或路径，不开放 helper 网络端口。
- [ ] 4.2 管理 Mac 上 Ubuntu ARM64 helper 生命周期和健康状态；TeacherConsole 设备健康页现可检查 Docker/镜像/容器/版本/校区私钥，并一键构建或启动 helper；应用启动和采集时可恢复已构建容器。Mac `.app` 打包 helper 脚本；可从原教师端导出匹配的 Veyon 私钥并保存到 macOS 用户钥匙串，再注入 helper tmpfs；注入通过管道传输，不落到宿主机普通文件，DER 与临时 PEM 缓冲区在使用后清零。本轮启动脚本与 TeacherConsole 健康/采集路径会拒绝特权容器、非 bridge 网络、发布端口或不匹配的私有 tmpfs；Mac Release 构建及本机启动脚本执行通过。2026-10-27 重新生成了 `1.1.38` 本机预览 `.app`，确认 helper 脚本已打入资源目录且与源码逐字节一致；该包未签名。当前容器使用固定 `veyon-campus-helper:4.11.4`，Veyon CLI 版本正确，私有 tmpfs 为 96 MiB、无发布端口且非特权；尚无校区私钥，也未验证真实认证、学生截图、应用重启恢复和校园 LAN。
- [ ] 4.3 Mac `ClassroomScreenPreviewCapture` 已注入 helper provider，PNG 会复用既有尺寸校验、JPEG 缩放、短期内存缓存和 no-store 响应；TeacherConsole `osx-arm64` Release 编译通过（0 警告、0 错误）。2026-10-27 合成 Veyon 学生端经完整 helper 返回有效 640×480 PNG，采集目录无残留；实际手机接口、真实学生画面和逐台展示仍待验证。
- [ ] 4.4 保留 Windows 本地 CLI 采集实现；两平台共用配对、租约、可见卡片调度、速率限制和审计规则。
- [ ] 4.5 在真实课堂验证全部座位缩略图、单台大图、暂停/切页/后台/撤销/改座位/断线取消和 10–70 台资源预算。

## 阶段 5：SukiUI / Flowery 界面改进

- [ ] 5.1 统一 StudentSetup 和 TeacherConsole 的 SukiUI 主题、基础控件、空/加载/错误/未知状态和键盘焦点。TeacherConsole 首次设置和健康页沿用 Suki `InfoBar`；StudentSetup 当前配置载入与部署错误已换为 Suki `InfoBar`。本轮新增应用级统一焦点色与 Button、TextBox、ComboBox、RadioButton、CheckBox 的键盘焦点边框；学生向导的步骤和自定义操作选项模板新增焦点框，教师侧栏焦点边框增强。Windows StudentSetup 与 macOS TeacherConsole Release 构建通过；运行时键盘导航、视觉对比度及屏幕阅读器核验待完成。
- [ ] 5.2 在适合的教师页面引入 Flowery `DaisyStat`、`DaisyStatusIndicator`、`DaisyAlert`、`DaisyProgress`；保留座位长列表虚拟化和现有 tokens。设备健康页已使用四种 Flowery 控件；课堂概览已改为四项 `DaisyStat`，展示课堂状态、电脑、回执和待处理数；首次设置向导进度改用 `DaisyProgress` 并提供当前步骤的自动化名称。macOS ARM64 Release 编译通过；运行截图、屏幕阅读器及跨平台显示检查待完成。
- [ ] 5.3 完善 TeacherConsole 首页、课堂/地点座位、策略、设备健康和首次设置页面；保持既有业务绑定及安全确认。课堂试点不可用提示已改用 Suki `InfoBar`；本轮将网站、应用和系统策略的成功/错误反馈统一为 `InfoBar`；具体页面的整体视觉检查和布局一致性待完成。
- [ ] 5.4 改善 StudentSetup 五步部署向导，突出目标、风险、准备进度和逐项结果，不改变安装/更新协议。配置载入、全局部署错误、配置建议及密码替换风险已使用 Suki `InfoBar`；当前步骤新增目标说明和进度文本，五个导航项提供屏幕阅读器名称与操作提示；本轮为自定义步骤导航和操作选择模板补上键盘焦点框。Windows x64 Release 构建通过；整体视觉、键盘焦点及逐步操作验收待完成。
- [ ] 5.5 提供 Windows 1366×768、Mac 桌面缩放和长中文文本的运行截图；检查状态文本、焦点、对比度和窗口边界。

## 阶段 6：手机页面

- [ ] 6.1 保留 PWA 四个主入口，统一课堂、限制、画面、设置的页面标题、状态条、触控目标和底部安全区。
- [ ] 6.2 重排手机策略流程，明确选择范围、刷新状态、策略确认及逐台结果；显示不可发送的具体原因。`/api/status`、`/api/policy` 的常见拒绝原因、配对/来源校验失败和 Mac 签名密钥缺失状态已有明确反馈；策略名单读写会显示服务端原因，状态/策略接口保留脱敏 5xx 原因。未填写姓名的电脑现在可按电脑名/IP 读取状态；只有目标重复或 Agent 身份/状态未验证时才阻止策略，手机会提示在教师端核对身份或更新学生端。手机策略页现可读取教师端保存的网站、应用、组合和长期系统预设，并向 `/api/policy` 提交带逐台预期版本的请求；应用阻止采用影响预览后二次确认，完成后显示逐台结果。移动 API fixture 覆盖了预设控件、`/api/profiles` 与 `/api/policy` 接线及审核状态静态合同，但没有验证真实 Agent 策略交付。真实手机和已迁移学生仍待验收。
- [ ] 6.3 完成双列 16:9 屏幕卡片、单台大图和固定的开始/暂停/停止操作；保持默认不采集。现有页面提供双列缩略图与单台查看；本轮加入暂停/继续控制，暂停时取消请求并保留最近一帧，继续时重新签发巡视租约；全班启动校验课堂 ID 和完整座位快照，单台启动再核对全班座位总数与画质，防止错误接受不匹配的租约。巡视期间开始/暂停/停止操作固定在手机底部导航上方，清晰度/间隔收进折叠区，服务端错误会显示具体原因。Chrome 手机视口模拟已检查 390×844 和 320×740 CSS px：文档无横向溢出、4 张卡片保持 16:9，长姓名最多显示两行，更新时间另起一行。合成画面与模拟 API 的预览保存在 [previews](previews/README.md)；真实画面、暂停交互和手机真机仍待验收。
- [ ] 6.4 检查 390px、窄屏、长学生姓名、未知/离线/部分成功、浏览器前后台和安卓/iOS 证书信任。长姓名和窄屏已通过上述合成预览核对；新增策略预设卡片已在 iPhone 15（iOS 17.5）Simulator Safari 的 393×852 CSS px 视口用本地 mock API 检查，截图见 [预览](previews/mobile-policy-presets-iphone15-393.png)。320px 策略卡片、部分成功状态、真实浏览器前后台和证书信任仍待验收。

## 阶段 7：发布和端到端验收

- [x] 7.1 准备 Mac TeacherConsole 安装/首次启动指南、Keychain 授权说明、Ubuntu ARM64 helper 配置与恢复步骤；已补充[Mac 教师端首次设置与屏幕巡视指南](../../docs/教师端Mac首次设置与屏幕巡视.md)，真实设备步骤仍按 7.3 现场验收。
- [ ] 7.2 保持 Windows TeacherConsole/StudentSetup 发布流程通过；Mac 构建不把 StudentSetup 或 Windows Worker 包进 Mac 产物。本轮 StudentSetup 与 TeacherConsole 均完成 `win-x64` locked restore 和 Release build（0 警告、0 错误）；2026-10-27 新增手机策略预设卡片与资源版本号后，重新执行 `osx-arm64` locked restore 和 TeacherConsole Release build，构建成功（0 警告、0 错误），并再次运行 `scripts/publish-teacher-macos.sh` 生成 `1.1.38` 本机 `.app`。发布脚本检查了角色标记及 Windows 载荷排除；包内有 `v51` 手机页面资源，helper 脚本与源码一致，Info.plist 有效。Mac 脚本支持 Developer ID 签名、hardened runtime、notarytool 公证、装订及系统评估，指南已记录启用步骤。本机没有有效签名身份，当前包未签名，只适合本机预览；跨设备分发和完整 Windows 发布流程仍待验收。
- [ ] 7.3 使用真实 Mac 教师机、Windows 学生机和配对手机走完整课堂、策略、座位编辑及屏幕巡视路径。
- [ ] 7.4 记录构建、UI 截图、网络/性能和安全恢复证据；未现场验证的项目继续标记待验收。
