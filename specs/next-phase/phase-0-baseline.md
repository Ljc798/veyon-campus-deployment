# 下一阶段 Phase 0：仓库基线与协议盘点

核查日期：2026-10-22  
基线分支/提交：develop / d4e08ed（secure student update LAN downloads）  
开始核查时工作区：干净

本文记录当前仓库事实和后续实现约束。设计文档中的新功能仍是待实现方案。

## 当前版本和验证基线

| 组件 | 当前源码版本 |
| --- | --- |
| Teacher / StudentSetup | 1.1.16 |
| Student Agent | 0.4.53 |
| Student Companion | 0.4.67 |
| Core | 0.4.51 |
| Worker | 0.4.63 |

本机使用 .NET SDK 10.0.401 x64 与 Node 22.17.1。

| 检查 | 基线结果 | 说明 |
| --- | --- | --- |
| .NET Release 解决方案构建 | 通过 | 0 个错误；存在 NU1900、Avalonia/XAML 和 CA1416 等既有警告 |
| TeacherConsole Release 构建 | 通过 | 0 个错误；存在 NuGet 漏洞数据源不可达和 TextBox.Watermark 过时警告 |
| MobileWeb app.js / service-worker.js 语法检查 | 通过 | Node --check |
| VeyonCampus.Checks 全量检查 | 未完成 | 默认临时目录解析受沙箱权限限制；改用仓库 .tmp 后前 4 项通过，随后 ApplicationReleaseChecks 因 Windows 符号链接权限不足中止。不能记录为全量通过 |

基线检查已尝试过，不把沙箱中的权限失败归因于产品行为。修改后的代码构建和可运行检查仍需继续；Windows 现场验收仍按各阶段门槛执行。

## 已有领域模型与兼容边界

| 领域 | 当前实现 | 对后续的约束 |
| --- | --- | --- |
| 校区/教室 | TeacherCampusProfile.ProfileId 和 TeacherRoomProfile.RoomId 是本机目录中的稳定 Guid | 保留现有 ID，不以地点显示名重建或覆盖已有 ID |
| 课堂会话 | ClassroomSession.SessionId；Room 快照保存 ProfileId、RoomId；TargetId 是会话内随机目标 ID | 已开始会话固定自己的目标快照，切换当前教室不能改投该会话 |
| 设备身份 | 当前没有跨会话稳定 DeviceId。TargetId 不能充当 DeviceId；Veyon 目标主机名/IP 是可变连接地址 | 后续设备身份应绑定已固定核验的 Agent 密钥及 Veyon 固定身份，不以 IP 为主键；冲突需人工核验 |
| 启动代次/控制代次 | 当前没有 BootId 或 ControlEpoch | 新协议启用前必须定义持久性、递增、重启和旧状态迁移规则；未定义前不得在 UI 中伪造值 |
| 课堂状态 | 新课堂状态只同步互动在线信息；旧 `ClassroomMode` 枚举和恢复账本仅用于读取历史状态及安全清理旧临时策略 | 快捷管控使用独立 `DeviceControlMode`；不再提供旧临时课堂策略的启用接口，也不改变原有恢复账本语义 |
| 长期策略 | 网站、应用和系统策略有各自现存写入/回执流程 | 快捷解除只能撤销其明确拥有的短期状态，不能清除长期基线 |
| 设备健康 | 已有按需签名健康读回、异常分组和修复流程 | 快捷控制状态不得借用健康分类表示；无回执仍为未知 |

当前没有覆盖所有新能力的通用版本能力协商机制。后端能力落地时，必须增加明确的能力声明/版本检查并在旧 Agent 上失败关闭；仅看 Agent 版本字符串不能证明支持。Phase 1 只调整 UI，不新增 Agent 命令，不新增 Mobile REST 路由；快捷控制入口在能力、状态读回和恢复流程完成前保持禁用。

## 现有手机 REST 路由盘点

路由统一由 TeacherMobileControlServer 提供；PWA 只访问教师端 LAN 服务，不直连 Student Agent。

| 类别 | 已有路由 |
| --- | --- |
| 配对/会话 | POST /api/pair、/api/pair-status、/api/mobile-resume、/api/mobile-forget、/api/logout；GET /api/session |
| 教室和策略目录 | GET /api/rooms、/api/profiles、/api/website-policy-lists；POST /api/website-policy-lists、/api/status、/api/policy、/api/policy/custom-website |
| 课堂会话/兼容恢复 | POST /api/classroom/session；旧 POST /api/classroom/mode 只接受恢复请求，启用旧课堂策略返回 410；GET /api/classroom/restores；POST /api/classroom/restores/retry |
| 课堂消息/互动 | GET/POST /api/classroom/events、/api/classroom/events/student、/api/classroom/events/reply、/api/classroom/events/notice、/api/classroom/chat、/api/classroom/quiz/student；GET /api/classroom/quiz |
| 屏幕预览 | POST /api/classroom/screen-preview/start、/stop；GET /api/classroom/screen-preview/{target} |

## 现有学生 Agent 路由盘点

Student Agent 由 WebsitePolicyRuntime 提供 HTTP.sys 端点，监听 TCP 39174。它并非供手机直接访问的 REST 服务；Teacher 负责签名、固定身份和网络转发。当前主要路径如下：

| 类别 | 已有 Agent 路由 |
| --- | --- |
| 网站策略/状态 | /v1/policy；/v1/status 及状态版本兼容路径 |
| 课堂状态/事件 | /v1/classroom/status；/v1/classroom/events/grant、/v1/classroom/events/local、/v1/classroom/events/local/resolve、/v1/classroom/events/local/chat、/v1/classroom/events/local/chat-delivered |
| 浏览器策略协作 | /v1/browser-restart/local、/v1/edge-policy-refresh/local、/v1/edge-policy/snapshot/local、/v1/edge-policy/apply-result/local、/v1/edge-policy/mode |
| 应用/系统策略 | /v1/application-policy、/v1/system-policy、/v1/application-policy/audit、/v1/application-inventory、/v1/student-accounts |
| 更新/健康修复 | /v1/update、/v1/student-update-download-capabilities、/v1–v3/update-timings、/v1/health/repair |
| 本机 Agent IPC | /v1/classroom/status/local、/v1/classroom/status/local/v2 及上表中以 /local 结尾的本机协作入口 |

健康快照作为现有签名状态读回的可选内容；健康修复是固定动作协议，不提供通用远程命令。本节路由以 WebsitePolicyRuntime 常量和注册处理为准，实际各路径的方法和签名要求不可由 UI 推断。

现有学生 Agent 的课堂状态、事件、网站/应用/系统策略、健康和更新通信均使用各自的签名、身份固定、版本或事务保护。Phase 1 不改变这些协议。新增手机命令仍必须经过 TeacherMobileControlServer 的授权、目标核对和教师端签名转发。/api/rooms 只提供 Veyon 地点名称和目标列表，不含 TeacherRoomProfile.RoomId；手机端的最近地点记忆因此只用于 UI 选择，不能作为设备身份或命令授权依据。同名地点和重复电脑目标必须停用并提示修正。

## Phase 1 实施边界

- TeacherConsole 默认打开现有课堂互动工作区，将其呈现为“课堂控制”；消息、求助和现有课堂会话保持原路由和行为。
- 常用导航保留课堂控制、设备健康、策略与预设、更新与运维；“地点与学生名单”和“首次设置向导”以带边框的一级入口显示在常驻“教室与部署”分组，手机配对与连接也在该分组内，不折叠隐藏。
- 当前教室数为 0 时显示配置引导；1 个时直接显示地点名称；多个时才显示选择器并记住最近选择。
- 教室切换只改变后续页面选择；既有 ClassroomSession 继续绑定开始时记录的 RoomId 和目标快照。
- 手机底部导航使用“课堂 / 设备 / 画面 / 我的”，保留现有策略、消息、配对、屏幕预览功能。地点选择沿用 /api/rooms 的真实读回；一个地点隐藏选择器，零个地点显示未配置提示。
- 快捷管控卡片只显示准备状态；网站/键鼠/解除按钮禁用，不调用现有 Practice API，也不预告未实现的结果。
- 每台结果、确认和错误类别的通用组件在后端命令协议确定后再接入；Phase 1 不制造假回执。

## Phase 1 实施状态（2026-10-22）

- 已完成 TeacherConsole 默认进入课堂控制、常用导航简化、“地点与学生名单”与“首次设置向导”提升为带边框的一级入口并归入“教室与部署”分组，以及桌面端 0/1/多个教室展示和按校区记忆最近选择。
- 已完成手机端四个底部标签调整、0/1/多个地点选择、最近地点记忆；同名地点会停用并提示，设备目标重复仍沿用现有禁用保护。
- 桌面和手机快捷管控入口均为禁用状态；未新增 Agent 命令或 Mobile REST 路由，现有课堂、策略、消息、配对和屏幕预览接口未更改。
- Release 解决方案与 TeacherConsole 构建通过，0 个错误；存在 NuGet 漏洞数据源不可达及既有 Avalonia/XAML 警告。`app.js`、`service-worker.js` 的 Node 语法检查通过；最近教室持久化定向检查通过。
- 全量 `VeyonCampus.Checks` 仍未完成：基线运行在 `ApplicationReleaseChecks` 创建 Windows 符号链接时因当前环境缺少权限中止。此限制不是产品检查通过的证据。
- 仍需在真实 TeacherConsole 窗口与手机 PWA 上人工确认：常用导航与部署入口可达、0/1/多个地点切换与记忆、切换教室时既有会话目标不变、重复地点/设备提示、已有互动/策略/屏幕功能，以及快捷管控始终不可点击。
