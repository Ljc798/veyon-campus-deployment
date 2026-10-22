# 下一阶段 Phase 3：InputGuard 原型与安全接入

实施记录：2026-10-22  
Phase 3 初始状态：独立本机可行性原型和交互会话 Guard 已构建；教师签名协议、Agent 状态/命令路由、Teacher 端单设备传输客户端、SYSTEM Agent 与登录会话 Guard 之间的 ACL 管道及安装包/进程清理接入已实现。Windows 输入恢复与安装升级行为尚未真机验收。

> 本记录和下方 Phase 3 安装包表格是快捷管控 UI 接入前的阶段快照。教师/手机操作入口、互斥状态服务和 Phase 4 本地测试包已在[Phase 4 记录](phase-4-classroom-quick-control.md)中实现；本页旧构建中的“按钮禁用”说明不适用于 Phase 4 测试安装器。

**2026-10-22 后续状态：** TeacherConsole 1.1.20 的设备健康卡片和手机状态页现在显示 Agent 签名回传的 InputGuard 阶段、租约到期时间及未知/退出状态。新版 Agent 使用 v2 轻量摘要；旧 Agent 继续使用 v1 并明确显示“不提供该状态”。教师快捷管控入口仍由 Phase 4 试点构建门控制。上述显示、实际键鼠恢复和部署升级仍待统一现场验收，见[统一现场验收清单](../../docs/下一阶段统一现场验收清单.md)。

## Windows 行为约束

- Windows `BlockInput(FALSE)` 只能由先前调用 `BlockInput(TRUE)` 的同一线程正常解除。
- Windows 会在阻止线程异常退出或用户按 `Ctrl+Alt+Delete` 时恢复输入；这定义了短期可恢复控制，不提供安全桌面拦截能力。
- Student Agent 的 SYSTEM 进程位于 Session 0。不能从 Session 0 直接调用 `BlockInput` 并假设它影响学生桌面；Microsoft 对从服务启动交互程序的说明要求把独立进程放到交互用户上下文，`WTSQueryUserToken` 仅适用于高度可信服务并需要严密关闭 token 句柄。
- 依据：[BlockInput](https://learn.microsoft.com/zh-cn/windows/win32/api/winuser/nf-winuser-blockinput)、[Interactive Services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)、[WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)、[CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)。

## 已实现的隔离原型

- 新项目 `src/VeyonCampus.InputGuard/` 生成 `VeyonCampus.StudentInputGuard.exe`，作为学生安装包内的交互会话 Guard，并保留隔离原型入口。
- 默认启动只显示说明。实际试用必须附加 `--run-prototype-once`，阅读警告并键入屏幕显示的精确确认词。
- 原型拒绝 Session 0 和非交互运行；固定最多 20 秒，不接受外部续租、命令、脚本或网络请求。
- `BlockInput(TRUE/FALSE)` 放在同一专用 STA 线程。结束时输出解除调用结果；Ctrl+Alt+Delete 保持为系统恢复路径。
- `--guard-server` 模式由已存在的 Student Companion 在用户登录会话中监管启动；Guard 空闲启动，不创建额外登录任务，也不会自动锁定。
- 源码使用 asInvoker 清单，不要求管理员权限。原型不会改音频设备、媒体进程、HID 驱动、注册表或策略。
- Core 定义教师签名命令契约、设备指纹、`AgentRunId` / `ControlEpoch` 绑定、短 TTL、Activate/Renew/Release 固定动作、20 秒租约上限、签名回执和有界 IPC 消息。Agent 对命令编号去重、核对 nonce 和代次；Teacher 端传输客户端先读签名状态并排除本机/回环目标。
- Agent 只连接唯一活动 WTS 交互会话；零个或多个活动会话、无管道响应或 Guard 进程身份不匹配时，状态为未知且不执行命令。Agent 只接受固定安装路径中的 Guard 服务端进程；Guard 只接受固定安装目录、Session 0 的 SYSTEM Agent 客户端。
- SYSTEM Agent 和当前用户通过带显式 ACL 的本机会话管道通信，拒绝远程客户端。Guard 的唯一 STA 线程负责 `BlockInput(TRUE/FALSE)`，使用单调时钟限制最多 20 秒，续期必须匹配同一租约；到期和进程退出不会自动重新锁定。
- StudentSetup 将 Guard 与其他学生组件一起安装、升级和清理；安装/卸载前先停止进程。Companion 退出时关闭本会话 Guard。
- `AgentRunId` 每次 Agent 启动时变化；`ControlEpoch` 随已观察到的状态变化和成功操作变化。它们用于防止旧命令重放，不是 Windows 对输入当前状态的独立证明。
- Teacher UI、手机控制和设备健康异常列表还没有接上 InputGuard 状态/操作；完成逐台确认、回执呈现、恢复路径和人工验收后再开放教师操作入口。

主要实现文件：

- `src/VeyonCampus.Core/StudentInputGuardProtocol.cs`
- `src/VeyonCampus.Core/StudentInputGuardIpcClient.cs`
- `src/VeyonCampus.Core/StudentInputGuardTransport.cs`
- `src/VeyonCampus.InputGuard/StudentInputGuardServer.cs`
- `src/VeyonCampus.Companion/StudentCompanionInputGuardSupervisor.cs`
- `src/VeyonCampus.Core/WebsitePolicyRuntime.cs`
- `scripts/package-windows-offline.ps1`
- `src/VeyonCampus.Core/VeyonCampusProcessCleanup.cs`

## 当前测试构建

| 产物 | 路径 | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| 教师端本地测试安装器（含醒目配置导航、Phase 2 和 InputGuard 签名传输客户端） | `artifacts/test-build-phase3-guard-runtime-20261022/VeyonCampus-TeacherConsole-1.1.16-win-x64.exe` | 69,284,130 bytes | `59840691726b6664ca7a7531840df349a7a010d00e5407c923a55844823019d7` |
| 学生端本地测试安装器（含交互会话 Guard、SYSTEM Agent 管道和安全生命周期清理） | `artifacts/test-build-phase3-guard-runtime-20261022/VeyonCampus-StudentSetup-1.1.16-win-x64.exe` | 67,152,093 bytes | `27216755ffd4914a7adf7031564254dd87ef27bec6e620bf8cd1370a5a497314` |
| 独立 InputGuard 原型包 | `artifacts/StudentInputGuardPrototype-1.1.16-20261022.zip` | 86,812 bytes | `2341a53bb69eebbbce6d122dc7aead9503b0741e14c15a7e0f15c46fb3c848e4` |

完整 Release 解决方案编译成功，0 错误；有 7 条已有警告（NuGet 漏洞源不可达、Companion XAML 资源及检查项目 CA1416）。TeacherConsole 保留 2 条 `TextBox.Watermark` 过时警告；StudentSetup 保留 1 条 Companion XAML 资源警告。学生端安装器现已包含独立 Guard 和 .NET 运行文件。打包流程核对了内嵌 Veyon 文件大小及 SHA-256，并确认所有组件不含 Developer Release PEM 公钥。未配置发布公钥，因此这两个测试安装器的应用内更新安全停用；安装器未上传。没有运行测试套件、安装安装器或启动 Guard；编译不代表 Windows 行为已验收。

## Phase 3 初始快照中的未实现项

- Teacher 操作确认和逐台结果 UI；手机端经 Teacher 服务授权并调用同一控制服务。
- 设备健康摘要中的 Guard 状态、签名回执、异常分组和恢复生命周期。
- Windows 10/11 实机验证：锁定与解除、租约到期、Ctrl+Alt+Delete、UAC、锁屏/切换用户、远程桌面、异常退出、音视频连续性及重启。
- StudentSetup 安装、升级、卸载时的 Guard 停止、文件替换、进程清理和配置保留验收。

本页以下原始建议针对 Phase 3 初始构建。当前普通构建仍关闭新的锁定操作；Phase 4/6 试点包会按构建标记开放操作，但教师端与手机端仍待统一现场验收，不能用于正式机房。

## Phase 3 初始构建验收范围

以下步骤对应上方记录的 1.1.16 阶段包；当前版本测试步骤以[统一现场验收清单](../../docs/下一阶段统一现场验收清单.md)为准。

请先在测试教师电脑和可恢复的学生测试电脑上验收以下已接入内容；不要使用正式机房电脑：

1. 安装教师端测试包并打开主窗口。左侧应有独立的“教室与部署”分组，“地点与学生名单”和“首次设置向导”均直接可见、可点击，不需要展开折叠菜单。检查切页后内容没有被侧栏或窗口边缘裁切。
2. 打开“地点与学生名单”，分别检查无地点、单地点和多个地点时的显示与切换。开始课堂互动后切换地点，确认当前会话仍使用开始时绑定的地点；重新读取电脑名单后检查长学生名和长电脑名不会撑破布局。
3. 在学生测试电脑以管理员身份打开 Student Setup，进入管理员维护并清除当前载入的配置包。若该机已安装 Agent 且保留本机校区配置，点击“更新/修复 Student Agent”应直接沿用本机配置与信任密钥；验证成功状态、现有校区策略和名单均保留。若本机没有已安装配置，才载入正确校区包后重试。Setup 自身更新与 Agent 更新是两个独立操作。
4. 在 4 台已接入的测试电脑上打开教师端“设备健康”，检查正常、需关注、未知/未收到回执使用不同颜色，异常详情可展开，长名单可通过搜索、状态筛选和分页定位。再按 [Phase 2 现场清单](phase-2-runtime-health.md) 逐步扩展到 15、24 台；70 台测试仅在机器和网络允许时进行。
5. 用 Student Setup 测试包更新一台可恢复的学生电脑。确认 Guard 进程在当前登录用户会话中启动并保持空闲；随后正常退出 Companion 或卸载测试组件，确认 Guard 随生命周期退出。当前 Teacher UI 尚无远程锁定入口，因此不应在本构建中测试远程锁定。
6. 如需验证实际输入阻止行为，只在临时/可恢复的 Windows 测试机运行下方独立原型验收步骤；准备好 Ctrl+Alt+Delete 恢复路径，并按屏幕提示确认。不要在课堂或生产电脑上运行。

安装器是本地测试包；没有发布私钥，应用内 Setup 更新关闭。Agent 更新按钮不依赖当前窗口载入配置包，但首次安装或本机配置缺失时仍需要正确校区包。

## 之后统一进行的真机验收

只在可恢复的 Windows 10/11 测试机、标准学生用户交互桌面进行。运行前先保存工作，并准备可用的 Ctrl+Alt+Delete 恢复路径。

1. 取消确认词或输入错误内容，确认不会阻止输入。
2. 正确确认后验证键盘和鼠标的应用输入在 20 秒内被阻止，并在租约到期后恢复；记录 Windows build、桌面会话和返回状态。
3. 按下 Ctrl+Alt+Delete，观察安全桌面切换后输入恢复；返回桌面确认原型不会重新锁定。
4. 进行一次 UAC 提示、锁屏/解锁、用户切换和远程桌面会话检查，记录支持边界；任何不确定状态记为未知。
5. 在输入阻止期间结束原型进程，确认 Windows 恢复输入。不要用正在运行的教学电脑进行此项。
6. 播放本地或网络视频并使用耳机音频；确认输入阻止不主动停止播放或改变音量。此项必须记为人工观察结果。
7. 重启测试机，确认原型不会自启动、不会恢复旧锁定。

上述均是待做现场验收，不是本次代码构建已经证明的行为。当前隔离原型只覆盖本机 20 秒行为；交互会话 Guard/Agent 管道虽已实现，仍没有教师端控制入口。新教师端安装器中的“地点与学生名单”“首次设置向导”应在“教室与部署”分组直接可见。
