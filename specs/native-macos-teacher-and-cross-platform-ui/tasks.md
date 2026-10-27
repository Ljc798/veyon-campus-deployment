# 原生 Mac 教师端与跨端课堂体验：实施任务

## 阶段 0：目标环境可行性

- [x] 0.1 确认 Apple Silicon 架构、macOS 版本、.NET 10 SDK 和可用虚拟化运行时（SDK 位于 `/Users/alex/.dotnet/dotnet`，Docker Desktop 可运行 ARM64 容器）。
- [ ] 0.2 建立可恢复的 Ubuntu ARM64 测试环境；验证 Veyon 固定版本 CLI、Key authentication、LAN DNS/端口和网络对象目录读写。正式 ARM64 镜像已从上游 4.11.4 源码构建，CLI 版本为 `4.11.4`；已安装 Ubuntu ARM64 的 QCA RSA 插件，容器可启动，`authkeys list` 和 `authkeys help import` 可运行，导入语法为 `import <KEY> [<FILE>]`。本轮重新运行启动脚本修复了已有容器缺少显式认证模式的问题，并核实 `Authentication/Method=1`、截图与密钥目录路径；容器为非特权 bridge 模式、无发布端口，密钥目录为 `rw,nosuid,nodev,noexec,size=96m` tmpfs。`authkeys list` 目前为空，尚无校区私钥；校园 LAN、真实密钥认证和目录读写仍待验证。
- [ ] 0.3 在无 Veyon 图形窗口的情况下采集一台 Windows 学生机 Screenshot；确认临时图片精确定位、读取、清理、超时和取消。本机 Veyon 4.11.4 CLI 帮助已确认调用顺序为 `feature start <HOST ADDRESS> <FEATURE>`，与 helper 脚本一致。2026-10-27 使用仅容器互通的 Docker 内部网络、临时容器 tmpfs 内生成的测试密钥和 Veyon 4.11.4 HeadlessVncServer 完成本地合成链路复现：发现 DISPLAY 未设置时 CLI 以 QCoreApplication 启动，截图功能因 Qt GUI pixmap 崩溃并产生 0 字节文件；helper 现在为 CLI 子进程指定 `DISPLAY=:99` 和 `QT_QPA_PLATFORM=offscreen`，同一采集脚本返回有效 640×480 PNG，且采集目录没有遗留原图。该结果验证了 Linux helper 到 Veyon Server 的合成截图链路；真实 Windows 学生桌面、校区密钥、校园 LAN、超时与取消仍待验证。
- [x] 0.4 选定 Veyon helper 为首选截图路径；若真实 LAN、密钥认证或截图链路验证失败，再实现 Student Agent/Companion 回退。当前还没有真实学生机截图证据。

## 阶段 1：跨平台教师主机

- [ ] 1.1 增加教师平台服务接口：安全密钥库、网络目录、Veyon 控制面、手机 HTTPS host、TLS 身份和登录启动偏好。已将 Windows Veyon CLI 单帧截图与 Mac helper 截图统一注入受限的 `ITeacherVeyonControlPlane`，通过 `ITeacherNetworkDirectory` 共用 Windows Veyon 目录或 Mac 本机地点档案，并用 `ITeacherMobileControlIdentityProvider` 分别提供 Windows 与 Mac 的私网地址发现和 TLS 身份创建。手机服务按注入的采集提供者判断缩略图能力，不再仅按 Windows 平台开放；安全密钥库、HTTPS host 生命周期及登录启动接口仍待抽象。
- [ ] 1.2 保留 Windows 实现为默认基线；把当前 Windows API/注册表/CNG/HttpSys 调用放入 Windows 实现中。StudentSetup 构建目标现排除 Mac 登录项、Veyon helper 截图、课堂预览采集器、教师控制接口、网络目录适配器和手机身份提供者；强制重新编译后的 `win-x64` Release 程序集中没有这些教师端专用类型。其余散落的 Windows API 仍待迁入平台实现。
- [ ] 1.3 为 macOS 增加 Keychain 密钥/证书管理及 `ApplicationData` 路径，不把策略私钥复制到普通明文文件。`VeyonCampusPaths` 现统一解析用户数据根目录：macOS 在系统 special folder 返回空时回退到当前用户的 `Library/Application Support`，拒绝写入相对路径。策略签名证书、Veyon 私钥和手机 TLS 根证书通过 `X509Store(CurrentUser\My)` 保存；macOS 上该存储对应当前用户默认钥匙串（[.NET 跨平台证书存储说明](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography)）。普通 JSON 状态文件只记录证书指纹/修订号，不含私钥；Developer ID 签名应用的钥匙串访问范围、应用重启读回和证书信任仍待实机验证。
- [ ] 1.4 为 Mac 增加 Kestrel 私网 HTTPS 服务和当前用户登录启动偏好；只绑定私有 LAN 地址，不自动开放公网或更改防火墙。Mac 现在只从活动以太网/Wi-Fi 接口选取 RFC1918 地址用于手机服务与证书 SAN，避免把隧道等非 LAN 接口作为连接地址；Mac 首次设置页已接入 macOS 13+ `SMAppService.mainApp` 开关和批准状态。签名包注册、实际接口枚举、私网 host 真机验收待完成。
- [ ] 1.5 为 TeacherConsole 增加 `osx-arm64` 发布目标和 `.app` 启动入口；StudentSetup 保持 `win-x64`。
- [ ] 1.6 启动时显示真实平台能力清单；无法执行的 Windows 本机安装/修复功能禁用并说明原因。Mac 首次设置页已用 Suki `InfoBar` 与 Flowery 状态点说明本机课堂、学生信任迁移、截图前置条件及 Windows-only 操作；本轮将 helper 实际状态和准备/恢复操作加入首次设置页，用户可直接处理手机屏幕巡视前置条件。全部平台能力和禁用入口运行时核验仍待完成。

## 阶段 2：Mac 课堂、目录与策略

- [ ] 2.0 完成教师策略公钥迁移：优先支持 Mac 基于原学生包生成更新配置并让学生端安全切换信任根；迁移未完成的学生必须逐台显示未信任，不能假报状态或策略成功。
- [x] 2.0a 增加 Mac 首次设置入口：读取原包清单、保留 Veyon 公钥与电脑前缀，使用 Mac 本机策略公钥生成更新包；旧学生包或公钥校验失败时停止。首次设置页会在选包后后台验证清单和策略公钥，展示校区/电脑前缀或具体失败原因，并在通过校验前禁用私钥导入及更新包生成；生成时仍会再次验证来源包未变化。
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
- [ ] 4.2 管理 Mac 上 Ubuntu ARM64 helper 生命周期和健康状态；TeacherConsole 设备健康页现可检查 Docker/镜像/容器/版本/校区私钥，并一键构建或启动 helper；本轮也在首次设置页加入同一准备/恢复入口并显示实际健康状态；应用启动和采集时可恢复已构建容器。Mac `.app` 打包 helper 脚本；可从原教师端导出匹配的 Veyon 私钥并保存到 macOS 用户钥匙串，再注入 helper tmpfs；注入通过管道传输，不落到宿主机普通文件，DER 与临时 PEM 缓冲区在使用后清零。本轮启动脚本与 TeacherConsole 健康/采集路径会拒绝特权容器、非 bridge 网络、发布端口或不匹配的私有 tmpfs；Mac Release 构建及本机启动脚本执行通过。2026-10-27 重新生成了 `1.1.38` 本机预览 `.app`，确认 helper 脚本已打入资源目录且与源码逐字节一致；该包未签名。当前容器使用固定 `veyon-campus-helper:4.11.4`；2026-10-27 从已停止状态恢复后确认运行中、非特权 bridge、无发布端口、96 MiB 私有 tmpfs、目录权限 700，Veyon CLI 为 4.11.4 且密钥文件认证已启用。当前钥匙清单为空，尚无校区私钥，也未验证真实认证、学生截图、应用重启恢复和校园 LAN。
- [ ] 4.3 Mac `ClassroomScreenPreviewCapture` 已注入 helper provider，PNG 会复用既有尺寸校验、JPEG 缩放、短期内存缓存和 no-store 响应；TeacherConsole `osx-arm64` Release 编译通过（0 警告、0 错误）。2026-10-27 合成 Veyon 学生端经完整 helper 返回有效 640×480 PNG，采集目录无残留；实际手机接口、真实学生画面和逐台展示仍待验证。
  - 2026-10-27 运行当前源码的 `--mobile-control-api-fixtures`：本机 HTTPS 服务、已授权手机请求、巡视租约、合成 JPEG 响应（含 `no-store`）和停止后的拒绝行为均通过。该 fixture 用合成采集回调，没有调用 Mac Veyon helper；当前 Docker daemon 未运行，所以本轮未串接 helper 与手机 API，真实学生画面仍待验证。检查项目此前漏链接教师网络目录和手机身份适配器，本轮已补齐。
- [ ] 4.4 保留 Windows 本地 CLI 采集实现；两平台共用配对、租约、可见卡片调度、速率限制和审计规则。
- [ ] 4.5 在真实课堂验证全部座位缩略图、单台大图、暂停/切页/后台/撤销/改座位/断线取消和 10–70 台资源预算。

## 阶段 5：SukiUI / Flowery 界面改进

- [ ] 5.1 统一 StudentSetup 和 TeacherConsole 的 SukiUI 主题、基础控件、空/加载/错误/未知状态和键盘焦点。TeacherConsole 首次设置和健康页沿用 Suki `InfoBar`；StudentSetup 当前配置载入与部署错误已换为 Suki `InfoBar`。本轮新增应用级统一焦点色与 Button、TextBox、ComboBox、RadioButton、CheckBox 的键盘焦点边框；学生向导的步骤和自定义操作选项模板新增焦点框，教师侧栏焦点边框增强。Windows StudentSetup 与 macOS TeacherConsole Release 构建通过；运行时键盘导航、视觉对比度及屏幕阅读器核验待完成。
- [ ] 5.2 在适合的教师页面引入 Flowery `DaisyStat`、`DaisyStatusIndicator`、`DaisyAlert`、`DaisyProgress`；保留座位长列表虚拟化和现有 tokens。设备健康页已使用四种 Flowery 控件；课堂概览已改为四项 `DaisyStat`，展示课堂状态、电脑、回执和待处理数；教师首次设置向导与学生部署页的整体进度均使用 `DaisyProgress`，并保留当前进度的自动化名称。StudentSetup 的环境检查、执行和结果列表使用 `DaisyStatusIndicator`，加载动画使用 Flowery `DaisyLoading`，并显示可读状态文字。Windows StudentSetup x64 Release 编译通过；macOS ARM64 TeacherConsole 编译通过。运行截图、屏幕阅读器及跨平台显示检查待完成。
- [ ] 5.3 完善 TeacherConsole 首页、课堂/地点座位、策略、设备健康和首次设置页面；保持既有业务绑定及安全确认。课堂试点不可用提示已改用 Suki `InfoBar`；本轮将网站、应用和系统策略的成功/错误反馈统一为 `InfoBar`；具体页面的整体视觉检查和布局一致性待完成。
- [ ] 5.4 改善 StudentSetup 五步部署向导，突出目标、风险、准备进度和逐项结果，不改变安装/更新协议。配置载入、全局部署错误、配置建议及密码替换风险已使用 Suki `InfoBar`；当前步骤新增目标说明和进度文本，五个导航项提供屏幕阅读器名称与操作提示；本轮为自定义步骤导航和操作选择模板补上键盘焦点框，整体部署进度使用 Flowery `DaisyProgress`，预检与部署加载态使用 Flowery `DaisyLoading`，并朗读当前进度、步骤和结果状态。Windows x64 Release 构建通过；整体视觉、键盘焦点及逐步操作验收待完成。
- [ ] 5.5 提供 Windows 1366×768、Mac 桌面缩放和长中文文本的运行截图；检查状态文本、焦点、对比度和窗口边界。本机自动化会话的 CoreGraphics 探测返回 `activeDisplays=0`；直接启动刚打包的 Mac 预览应用时，Avalonia.Native 12.1.3 因无法启动 RenderTimer（错误码 `-6661`）退出，屏幕截图为黑屏。此错误与 [Avalonia 在 macOS 无活动显示器时的上游记录](https://github.com/AvaloniaUI/Avalonia/issues/18895)吻合，不能作为正常桌面运行失败或视觉通过的证据。仍需在有活动显示器的 macOS 图形会话中截图，并补齐 Windows 1366×768 检查。

## 阶段 6：手机页面

- [ ] 6.1 保留 PWA 四个主入口，统一课堂、限制、画面、设置的页面标题、状态条、触控目标和底部安全区。
- [ ] 6.2 重排手机策略流程，明确选择范围、刷新状态、策略确认及逐台结果；显示不可发送的具体原因。`/api/status`、`/api/policy` 的常见拒绝原因、配对/来源校验失败和 Mac 签名密钥缺失状态已有明确反馈；策略名单读写会显示服务端原因，状态/策略接口保留脱敏 5xx 原因。未填写姓名的电脑现在可按电脑名/IP 读取状态；只有目标重复或 Agent 身份/状态未验证时才阻止策略，手机会提示在教师端核对身份或更新学生端。手机策略页现可读取教师端保存的网站、应用、组合和长期系统预设，并向 `/api/policy` 提交带逐台预期版本的请求；应用阻止采用影响预览后二次确认，完成后显示逐台结果。移动 API fixture 覆盖了预设控件、`/api/profiles` 与 `/api/policy` 接线及审核状态静态合同，但没有验证真实 Agent 策略交付。真实手机和已迁移学生仍待验收。
- [ ] 6.3 完成双列 16:9 屏幕卡片、单台大图和固定的开始/暂停/停止操作；保持默认不采集。现有页面提供双列缩略图与单台查看；本轮加入暂停/继续控制，暂停时取消请求并保留最近一帧，继续时重新签发巡视租约；全班启动校验课堂 ID 和完整座位快照，单台启动再核对全班座位总数与画质，防止错误接受不匹配的租约。巡视期间开始/暂停/停止操作固定在手机底部导航上方，清晰度/间隔收进折叠区，服务端错误会显示具体原因。Chrome 手机视口模拟已检查 390×844 和 320×740 CSS px：文档无横向溢出、4 张卡片保持 16:9，长姓名最多显示两行，更新时间另起一行。合成画面与模拟 API 的预览保存在 [previews](previews/README.md)；真实画面、暂停交互和手机真机仍待验收。
  - 2026-10-27 增补：巡视期间离开视口的卡片会保留最近画面并标记为旧帧，回到视口后自动重新读取；手机内帧缓存以 8 MiB 为回收预算，优先释放离屏旧帧并保护当前可见画面。暂停会标记保留帧，停止巡视或页面进入后台仍清除全部画面。无帧卡片使用“暂无画面”占位；首次读取时新增骨架光带与读取文字，尊重减少动态效果偏好。隔离 Chrome 使用当前 PWA 源码、合成课堂会话和 mock 画面 API，在 320×740 CSS px 确认两张可见卡片显示加载状态、视口无横向溢出，减少动态效果时关闭动画；随后返回合成 JPEG，确认两张卡片加载完成且光带清除。截图见 [首次读取状态预览](previews/mobile-monitor-loading-320.png)。之前的合成 JPEG 会话已在 390×844、320×740 CSS px 验证离屏保留、返回后二次读取、8 MiB 回收预算和无横向溢出；真实手机、教师 API 和 Veyon 学生采集仍待验收。
- [ ] 6.4 检查 390px、窄屏、长学生姓名、未知/离线/部分成功、浏览器前后台和安卓/iOS 证书信任。长姓名和手机画面卡片窄屏已通过合成预览核对；策略预设卡片已在 iPhone 15（iOS 17.5）Simulator Safari 中用本地 mock API 检查 393×852 CSS px 视口，并通过 320×852 CSS px iframe 检查窄屏排版，预设名称完整显示且刷新按钮单独占行，截图见 [393px 预览](previews/mobile-policy-presets-iphone15-393.png) 与 [320px 预览](previews/mobile-policy-presets-320.png)。未知/离线/部分成功状态、真实浏览器前后台和安卓/iOS 证书信任仍待验收。
  - 2026-10-27 补充：巡视卡片更新时间和聊天消息时间使用中文 24 小时格式，避免中文页面跟随设备语言显示 AM/PM。已从当前 PWA 源码重新生成 390×844 与 320×740 Chrome DevTools 手机视口预览；设备模拟测得两种视口下文档宽度均与视口一致，截图显示 `11:52` 且无 AM/PM。预览使用临时 mock API 和合成屏幕图像，不代表真实手机或学生机采集。

## 阶段 7：发布和端到端验收

- [x] 7.1 准备 Mac TeacherConsole 安装/首次启动指南、Keychain 授权说明、Ubuntu ARM64 helper 配置与恢复步骤；已补充[Mac 教师端首次设置与屏幕巡视指南](../../docs/教师端Mac首次设置与屏幕巡视.md)，真实设备步骤仍按 7.3 现场验收。
- [ ] 7.2 保持 Windows TeacherConsole/StudentSetup 发布流程通过；Mac 构建不把 StudentSetup 或 Windows Worker 包进 Mac 产物。2026-10-27 手机策略预设卡片和 320px 窄屏调整后，`StudentSetup`、Windows `TeacherConsole` 与 Mac `TeacherConsole` 的 locked restore/Release build 均成功（0 警告、0 错误）；新增平台截图接口后强制重编 `StudentSetup`，确认程序集不含教师 Veyon helper 类型。新增 `ITeacherNetworkDirectory` 后再次发布 Mac `osx-arm64` TeacherConsole，并重建 Windows TeacherConsole/StudentSetup；Mac 发布成功，Windows 两个构建均为 0 警告、0 错误。教师程序集包含目录适配器，StudentSetup 程序集不含其接口或实现。新增 `ITeacherMobileControlIdentityProvider` 后再次发布 Mac `.app` 并重建两个 Windows 角色；Mac 发布成功，Windows TeacherConsole/StudentSetup 均为 0 警告、0 错误。Mac 教师程序集包含身份接口和 Windows/Mac 提供者，StudentSetup 程序集不含接口或平台实现，`Info.plist` 检查通过。再次运行 `scripts/publish-teacher-macos.sh` 生成 `1.1.38` 本机 `.app`；包内嵌入的 PWA HTML 和 service worker 均为 `v53`，helper 脚本与源码一致，Info.plist 有效。Mac 脚本支持 Developer ID 签名、hardened runtime、notarytool 公证、装订及系统评估，指南已记录启用步骤。本轮将 helper 健康状态和准备/恢复入口加入首次设置页；TeacherConsole `osx-arm64` locked restore、Release build 和 `.app` 打包成功，构建 0 警告、0 错误。当前将 Mac 来源包校验移到后台并在信任迁移页面显示结果；再次执行 macOS TeacherConsole Release build 与 `.app` 打包成功，构建 0 警告、0 错误。`VeyonCampusPaths` 本轮统一了用户数据根目录并加入 macOS fallback；Mac TeacherConsole、Windows StudentSetup 和 Windows TeacherConsole 的 locked restore/Release build 均成功（各 0 警告、0 错误），`1.1.38` Mac `.app` 重新打包成功（未签名）。未重新运行图形界面截图。当前包未签名，只适合本机预览；跨设备分发和完整 Windows 发布流程仍待验收。
  - 2026-10-27 补充：本轮手机巡视卡片改动已通过 macOS `osx-arm64` Release locked restore 与 `.app` 重新打包，生成的 1.1.38 应用仍未签名，仅限本机预览；本轮未启动该包做原生窗口验收。学生部署页新增 Flowery `DaisyProgress` 后，Windows `StudentSetup` x64 Release 构建通过（0 警告、0 错误）；未在 Windows 桌面运行该向导做视觉或屏幕阅读器验收。
  - 2026-10-27 补充：StudentSetup 环境检查、部署执行及结果列表新增 Flowery 状态点和可见状态文字；Windows `StudentSetup` x64 与 macOS `TeacherConsole` ARM64 Release build 均通过（0 警告、0 错误）。未在 Windows 桌面或活动显示器的 Mac 图形会话中检查实际布局或屏幕阅读器朗读。
  - 2026-10-27 补充：手机首次取帧增加骨架光带与读取文字；隔离 Chrome 合成课堂模拟确认 320×740 视口无溢出、减少动态效果时关闭光带，延迟返回 JPEG 后两张可见卡片加载成功且光带清除。Mac 1.1.38 .app 已重打包，嵌入的 PWA HTML、CSS 与 Service Worker 使用 v53；发布成功但未签名，仅限本机预览。未验证真实手机、学生采集或原生窗口。
  - 2026-10-27 补充：StudentSetup 的预检与部署加载态改为 Flowery DaisyLoading，移除 120 ms Unicode 帧轮换计时器。Windows StudentSetup x64 locked restore/Release build 通过（0 警告、0 错误）；Mac TeacherConsole ARM64 发布成功并重打包 1.1.38 .app（未签名）。Windows/Mac 实际窗口布局和读屏验收待完成。
- [ ] 7.3 使用真实 Mac 教师机、Windows 学生机和配对手机走完整课堂、策略、座位编辑及屏幕巡视路径。
- [ ] 7.4 记录构建、UI 截图、网络/性能和安全恢复证据；未现场验证的项目继续标记待验收。
