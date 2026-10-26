# 原生 Mac 教师端与跨端课堂体验：实施任务

## 阶段 0：目标环境可行性

- [x] 0.1 确认 Apple Silicon 架构、macOS 版本、.NET 10 SDK 和可用虚拟化运行时（SDK 位于 `/Users/alex/.dotnet/dotnet`，Docker Desktop 可运行 ARM64 容器）。
- [ ] 0.2 建立可恢复的 Ubuntu ARM64 测试环境；验证 Veyon 固定版本 CLI、Key authentication、LAN DNS/端口和网络对象目录读写。当前 Ubuntu/Launchpad 源 DNS 解析失败，Veyon 包尚未安装。
- [ ] 0.3 在无 Veyon 图形窗口的情况下采集一台 Windows 学生机 Screenshot；确认临时图片精确定位、读取、清理、超时和取消。
- [ ] 0.4 记录是否采用 Veyon helper 或 Student Agent/Companion 截图回退；失败项保留为平台约束，不伪记完成。

## 阶段 1：跨平台教师主机

- [ ] 1.1 增加教师平台服务接口：安全密钥库、网络目录、Veyon 控制面、手机 HTTPS host、TLS 身份和登录启动偏好。
- [ ] 1.2 保留 Windows 实现为默认基线；把当前 Windows API/注册表/CNG/HttpSys 调用放入 Windows 实现中。
- [ ] 1.3 为 macOS 增加 Keychain 密钥/证书管理及 `ApplicationData` 路径，不把策略私钥复制到普通明文文件。
- [ ] 1.4 为 Mac 增加 Kestrel 私网 HTTPS 服务和 LaunchAgent 启动偏好；只绑定私有 LAN 地址，不自动开放公网或更改防火墙。
- [ ] 1.5 为 TeacherConsole 增加 `osx-arm64` 发布目标和 `.app` 启动入口；StudentSetup 保持 `win-x64`。
- [ ] 1.6 启动时显示真实平台能力清单；无法执行的 Windows 本机安装/修复功能禁用并说明原因。

## 阶段 2：Mac 课堂、目录与策略

- [ ] 2.0 设计并实现教师策略公钥迁移：Windows 旧教师密钥签名授权学生 Agent 接受 Mac 公钥；迁移未完成的学生必须显示为未信任，不能假报状态或策略成功。
- [ ] 2.1 以 `CampusDirectoryStore` 和座位表提供 Mac 课堂目录；Veyon 目录由平台适配器同步。
- [ ] 2.2 让同一组活动课堂、座位身份和策略服务可运行在 Mac；不把 Windows Veyon CLI 作为课堂页面可见性的前置条件。
- [ ] 2.3 在 Mac 上读取签名 Agent 状态；对不支持轻量摘要的 Agent 回退完整签名状态，保留身份固定和逐台未知状态。
- [ ] 2.4 在 Mac 上签发和发送现有网站、应用、长期系统策略；保留预期版本比较、教师确认、审计和 Agent 签名校验。
- [ ] 2.5 验证手机配对、撤销、状态读回和网站/应用/系统策略的逐台成功/离线/冲突/未知结果。

## 阶段 3：地点和座位编辑

- [x] 3.1 允许课堂持续时打开座位图编辑；当前代码的 `CanEditSeatMap`、`CanToggleSeatMapEditing` 未以活动课堂作为禁用条件。
- [x] 3.2 在地点页提供当前地点资料编辑器；课堂持续时保存前明确确认，再由现有课堂范围协调流程同步。
- [ ] 3.3 确认提交时验证新地点和学生绑定、原子切换课堂目标、刷新手机状态，并撤销基于旧座位的巡视租约。
- [ ] 3.4 验证保存失败、重复目标、课堂切换和策略执行并发时旧目标保持可用且不会混淆学生身份。

## 阶段 4：Veyon helper 与屏幕缩略图

- [ ] 4.1 定义只允许读取/同步目录和 Screenshot 的本机 helper 协议；拒绝任意命令、路径和非课堂目标。
- [ ] 4.2 管理 Mac 上 Ubuntu ARM64 helper 生命周期和健康状态；使用短期认证通道和有界消息。
- [ ] 4.3 让 Mac `ClassroomScreenPreviewCapture` 通过平台控制面取得 PNG 字节，再沿用最大尺寸限制、JPEG 缩放、短期内存缓存和 no-store 响应。
- [ ] 4.4 保留 Windows 本地 CLI 采集实现；两平台共用配对、租约、可见卡片调度、速率限制和审计规则。
- [ ] 4.5 在真实课堂验证全部座位缩略图、单台大图、暂停/切页/后台/撤销/改座位/断线取消和 10–70 台资源预算。

## 阶段 5：SukiUI / Flowery 界面改进

- [ ] 5.1 统一 StudentSetup 和 TeacherConsole 的 SukiUI 主题、基础控件、空/加载/错误/未知状态和键盘焦点。
- [ ] 5.2 在适合的教师页面引入 Flowery `DaisyStat`、`DaisyStatusIndicator`、`DaisyAlert`、`DaisyProgress`；保留座位长列表虚拟化和现有 tokens。
- [ ] 5.3 完善 TeacherConsole 首页、课堂/地点座位、策略、设备健康和首次设置页面；保持既有业务绑定及安全确认。
- [ ] 5.4 改善 StudentSetup 三步部署向导，突出目标、风险、准备进度和逐项结果，不改变安装/更新协议。
- [ ] 5.5 提供 Windows 1366×768、Mac 桌面缩放和长中文文本的运行截图；检查状态文本、焦点、对比度和窗口边界。

## 阶段 6：手机页面

- [ ] 6.1 保留 PWA 四个主入口，统一课堂、限制、画面、设置的页面标题、状态条、触控目标和底部安全区。
- [ ] 6.2 重排手机策略流程，明确选择范围、刷新状态、策略确认及逐台结果；显示不可发送的具体原因。
- [ ] 6.3 完成双列 16:9 屏幕卡片、单台大图和固定的开始/暂停/停止操作；保持默认不采集。
- [ ] 6.4 检查 390px、窄屏、长学生姓名、未知/离线/部分成功、浏览器前后台和安卓/iOS 证书信任。

## 阶段 7：发布和端到端验收

- [ ] 7.1 准备 Mac TeacherConsole 安装/首次启动指南、Keychain 授权说明、Ubuntu ARM64 helper 配置与恢复步骤。
- [ ] 7.2 保持 Windows TeacherConsole/StudentSetup 发布流程通过；Mac 构建不把 StudentSetup 或 Windows Worker 包进 Mac 产物。
- [ ] 7.3 使用真实 Mac 教师机、Windows 学生机和配对手机走完整课堂、策略、座位编辑及屏幕巡视路径。
- [ ] 7.4 记录构建、UI 截图、网络/性能和安全恢复证据；未现场验证的项目继续标记待验收。
