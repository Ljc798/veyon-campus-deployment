# 下一阶段 Phase 2：轻量设备状态与异常中心

实施记录：2026-10-22  
当前状态：轻量健康、异常中心与 BootId 代码已完成；BootId 配套学生包为 StudentSetup 1.1.25，教师姓名解析修正版为 TeacherConsole 1.1.26；15 / 24 / 70 台现场验收待统一测试。

## 已实现

- Phase 2 初版由 Agent 提供 `POST /v1/device/runtime-summary`。请求复用已签名的状态 v4 身份、校区和随机数校验；Agent 回复另用已固定的 Agent 身份签名。摘要含 Agent / Setup 版本、Veyon 服务、网站与应用修订、系统策略待处理状态、Edge Profile 降级计数和更新事务状态。
- 轻量通道不读取域名清单、逐 Profile 诊断、SYSTEM 任务身份与 ACL 健康快照，也不包含原始日志。教师仍可按需点击“读取完整诊断”取得已有 v4 健康快照。
- Teacher 仅在设备健康页可见且未最小化时自动轮询。每 5 秒处理最多 12 台；UI 自动轮询与手机状态请求共享 12 路并发上限。单目标超时 1.5 秒；失败按 5 / 10 / 20 / 60 秒退避；45 秒无新签名摘要后标记过期。对无缓存设备，连续三次失败后才产生未响应异常；此前仍显示未知/待确认，绝不显示正常。
- 健康行维护 `Issues[]`，把当前可读到的 Veyon、系统策略、Edge 和更新问题分开呈现。相同问题按类型归并；重复采集更新最近时间，不反复增加事件数；新增受影响设备会清除该异常的已知悉状态。异常恢复后从当前列表移除，并可跳到首台受影响设备。
- 手机 `/api/status` 在正式 Teacher 窗口中调用 Teacher 端聚合器读取这些签名摘要，不再让手机连接 Student Agent；教师端网络转发使用同一并发门。轻量摘要不含网站域名清单和逐 Profile 细节，手机详情页会明确说明。
- InputGuard 状态通过 v2 摘要提供 `AgentRunId`、`ControlEpoch`、Guard 阶段及租约信息；旧 Agent 继续走 v1 摘要并明确显示不提供 Guard 状态。
- 1.1.25 在签名摘要中增加可选 `BootId`。当前 Agent 用机器名和 `Environment.TickCount64` 估算的 Windows 启动时间生成 SHA-256 标识，并在进程内缓存；同一系统启动期间 Agent 重启不会改变它。旧 Agent 不带该字段时，其他签名摘要仍可正常读取，不显示重启异常。
- Teacher 按设备比较已验证的 `BootId`。发生变化后创建待知悉的“学生电脑已重启”异常；如果上一次新鲜签名摘要证明 InputGuard 仍有有效锁定租约，则明确提示重启后不会自动恢复锁定。该事件不会发送锁定命令，教师知悉后从当前异常列表移除。
- BootId 是本地匿名标识，不包含在界面文本中。它由系统 UTC 与单调运行时间估算；大幅手动校时可能改变估算值，因此现场验收需覆盖 Agent 单独重启、Windows 重启和时间同步，确认无误报。

## 实现文件

- `src/VeyonCampus.Core/DeviceRuntimeSummaryProtocol.cs`
- `src/VeyonCampus.Core/WebsitePolicyRuntime.cs`
- `src/VeyonCampus.App/TeacherViewModel.cs`
- `src/VeyonCampus.App/TeacherWindow.axaml`
- `src/VeyonCampus.App/TeacherWindow.axaml.cs`
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`
- `src/VeyonCampus.App/MobileWeb/app.js`
- `src/VeyonCampus.App/MobileWeb/index.html`
- `src/VeyonCampus.App/MobileWeb/service-worker.js`
- `src/VeyonCampus.Agent/VeyonCampus.Agent.csproj`
- `src/VeyonCampus.InputGuard/VeyonCampus.InputGuard.csproj`

## 构建产物与证据

- StudentSetup 和 TeacherConsole 的 Windows x64 Release 安装器均生成于 `artifacts/test-build-phase2-20261022/`，版本 `1.1.16`：

  | 文件 | 大小 | SHA-256 |
  | --- | ---: | --- |
  | `VeyonCampus-StudentSetup-1.1.16-win-x64.exe` | 67,106,389 bytes | `7ab3561effae929e508749074b383b730f1bbe56bfaf6b094acbd4d58f248bf4` |
  | `VeyonCampus-TeacherConsole-1.1.16-win-x64.exe` | 69,260,557 bytes | `19a5ab4501a6ad9d4b8cd87e12301e1e3d6a8c0d0418ec45e45f181c6fa8462e` |

- 两端安装器由 `scripts/package-windows-offline.ps1` 构建，并完成其内嵌 Veyon 资源和更新助手自检。TeacherConsole 目标编译成功，保留 2 个既有 `TextBox.Watermark` 过时警告；StudentSetup 编译期间保留 1 个既有 Companion XAML 资源警告。
- 本机未配置 Developer Release 公钥，因此这些是本地测试包，应用内更新安全停用；不要作为正式发布包分发。
- 本阶段尚未运行自动检查套件或 Windows / 手机现场验收；不得把编译成功记为协议和实机行为通过。

## 2026-10-22 BootId 增量构建

- TeacherConsole / StudentSetup 版本为 `1.1.25`，Agent `0.4.55`，Core `0.4.53`，InputGuard `1.1.25`。
- Release 编译：成功，0 个错误。离线安装器均由 `scripts/package-windows-offline.ps1` 生成，完成 Veyon 嵌入资源与更新助手检查。

  | 文件 | 大小 | SHA-256 |
  | --- | ---: | --- |
  | `artifacts/test-build-phase2-boot-identity-20261022/VeyonCampus-StudentSetup-1.1.25-Boot-Identity-Test.exe` | 67,192,077 bytes | `410A377CA53CA3A155B4E3722E60E1E3E248DBD6D0B01541FE2E3A020A70AB65` |
  | `artifacts/test-build-phase2-boot-identity-20261022/VeyonCampus-TeacherConsole-1.1.25-Boot-Identity-Test.exe` | 69,335,346 bytes | `5D88B49C0CA67493F4F315FAF7CFA3D0443CB387EE2ECC9E15FC4AAFEB464700` |

- TeacherConsole 包沿用课堂快捷管控试点构建标记。缺少 Developer Release PEM 公钥，包内应用更新安全停用；安装器只用于可恢复测试设备。未启动或安装程序、未运行测试套件、未做现场验收、未上传任何远端。

## 统一现场测试清单

1. 用新版 StudentSetup 更新 4 台混合版本学生电脑上的 Agent；未更新 Agent 应明确保持未知并提示通过管理员维护更新。
2. 分别选取 15、24、70 台，打开设备健康页。确认列表逐批出现、最多 12 台同时请求、页面离开或 Teacher 最小化后停止轮询，返回页面后继续；长时间运行时没有请求排队、UI 卡顿或 Veyon 控制受影响。
3. 选一台断网设备：前两次失败保持未知/待确认；第三次出现未响应异常；状态超过 45 秒后标为过期。恢复网络后回到签名响应状态并清除对应异常。
4. 在多台电脑上分别制造 Veyon 服务停止、Windows 策略待处理、Edge Profile 降级和更新事务需恢复。确认同一台可同时显示多个异常；未知服务状态不会计为正常。
5. 标记异常已知悉，观察连续轮询不会增加发生次数；加入另一台同类故障会重新要求知悉；恢复故障后异常消失，点击“查看设备”能定位到受影响电脑。
6. 手动点击“读取完整诊断”，确认 SYSTEM 任务身份、ACL 和详细修复条件仍由 v4 诊断读取；仅凭轻量摘要不会启用修复按钮。
7. 已配对手机读取设备状态，确认状态显示来自教师端聚合、签名与时间正确，手机无法直连学生 Agent；检查网站/应用修订可继续用于教师端确认流程，详细域名和 Edge Profile 明细会说明未包含在轻量摘要。
8. 在正常与断网情况下重复读取手机状态，确认教师端总并发不超过 12、单目标超时与移动连接限速生效，断网不阻塞其他课堂消息与操作。
9. 用配套 StudentSetup 1.1.25 检查 Guard 锁定、解除、过期和退出状态是否随签名摘要显示；再选一台旧 Agent，确认它显示“此 Agent 版本未提供”，不会误报已解锁。
10. 用配套 1.1.25 包验证启动标识：只重启 Student Agent 服务，确认同一 Windows 启动期间不产生“学生电脑已重启”；再在有效 InputGuard 锁定租约期间重启 Windows，确认新 BootId 产生待知悉异常、提醒锁定不会自动恢复且不会自动重新锁定。
11. 确认教师知悉后异常从当前列表移除；旧 Agent 缺少 BootId 时摘要仍正常、不会产生启动异常。重复 Agent 重启及系统时间同步后检查是否有误报。异常设备名称仍只显示映射的学生姓名或“未填写姓名”。
