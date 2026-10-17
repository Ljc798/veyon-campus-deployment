# Edge 实时网站限制实施状态

更新时间：2026-10-17

## 当前范围

按用户要求，暂不开展真实学生机/Edge 的 P0 实际验收，继续推进后续源码实施。当前提交的是实现准备，不是生产启用结论。

已接入的源码路径：

- MV3 扩展编译 Blacklist、Allowlist、Combined、Disabled 的顶层导航 DNR 规则，原子替换并读回核对。
- 独立 Native Messaging Host 通过 stdio 与扩展通信，并转发到 Student Agent 已有 loopback 端口；没有增加 LAN 监听端口。
- Student Agent 提供签名策略快照、扩展回执与逐 Profile 状态；Edge 首次迁移使用教师签名命令及注册表 Pending 事务，只清理本工具拥有的 Edge 名单。
- 迁移完成后，Edge 后续策略更新跳过旧注册表 Edge 列表写入与 Edge 重启请求；Chrome/Firefox 保持现有路径。
- 学生安装包具备 Native Host/扩展文件；仅在构建时传入已发布的 Edge 扩展 ID 后才注册 Host 并添加 Edge 强制安装策略。
- 教师端逐台状态已接入；迁移入口只会在显式构建门控打开时出现。

## 当前门禁与未完成项

- 默认离线包不传入 Edge 扩展 ID，也不打开 `VeyonCampusEdgeRealtimeGatePassed`；教师端迁移入口隐藏，Agent 默认继续 `LegacyRegistry`。
- P0 实机测试未执行：尚未验证非 AD Windows 11 商店强制安装、标准学生账号保护、同一 Edge 进程中的真实拦截/恢复、多个 Profile/InPrivate 与扩展升级稳定性。
- 已加入教师端显式恢复入口：使用教师签名、策略版本绑定的命令，将最近签收的 Edge 规则通过事务写回注册表；扩展继续同步同一份策略，浏览器不重启。完整学生端和 Edge 实机恢复仍待 P0 验收。
- Native Host、Inno Setup 安装/卸载与完整 Windows 更新路径尚未在目标机验证。
- MV3 规则目前只约束新发起的顶层 HTTP(S) 导航；不会结束已经加载的页面，也不覆盖子资源、其他浏览器或任意应用。

## 手动试用包

- `scripts/package-windows-offline.ps1 -EdgeRealtimeManualTest` 可分别构建学生端和教师端 Edge 手动试用包；Beta 发布工作流使用此开关，该路径不会打开正式 `EdgeRealtimeGatePassed` 门禁。
- 学生试用包会使用单独的固定扩展公钥生成稳定 ID，安装本机 Native Messaging Host，但跳过 Edge Add-ons 强制安装策略；试用者需要在 `edge://extensions` 手动加载解压扩展。
- 教师试用包显示“启用 Edge 实时同步（试用）”与“恢复 Edge 注册表策略”两个操作。仅用于可还原的测试电脑，不能用于正式部署。
- 学生包和教师包随附 `EDGE-REALTIME-MANUAL-TEST.md` 操作说明。手动试用路径尚未完成真实 Edge 验收。

## 开启门控前的顺序

1. 完成并审查回滚路径：从 Edge 实时执行器安全恢复旧注册表策略，不重启浏览器，并验证外部学校策略不被覆盖。
2. 恢复并完成设计文档的 P0 真机验收，记录 Proceed/Blocked 结论及 Edge/Windows 版本、账号类型、安装渠道和浏览器 Profile 覆盖结果。
3. 在具备可用 .NET 10.0.100 SDK 和 Inno Setup 的 Windows 构建环境完成 StudentSetup/TeacherConsole 安装包检查。
4. 只有以上工作完成并复核后，才可以配置正式 Edge 商店扩展 ID 并打开教师迁移构建门控。

## 本轮静态检查

本轮网站注册表事务夹具通过（包含 Edge 恢复事务中断恢复及签名命令边界）；教师端和学生端 Windows x64 试用安装包均已本地编译。全量检查运行到自更新符号链接夹具时被本机缺少创建符号链接的权限中断；Beta CI 会在 Windows runner 上重跑全量检查。Edge/Windows P0 实机验收仍待用户完成，不能以本机打包结果代替。
