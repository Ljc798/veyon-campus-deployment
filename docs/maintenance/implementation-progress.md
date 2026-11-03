# Maintenance 连续实施进度

主计划：[完整实施计划（Codex 连续开发版）](../VeyonCampus_Maintenance_%25E5%25AE%258C%25E6%2595%25B4%25E5%25AE%259E%25E6%2596%25BD%25E8%25AE%25A1%25E5%2588%2592_Codex%25E8%25BF%259E%25E7%25BB%25AD%25E5%25BC%2580%25E5%258F%2591%25E7%2589%2588.md)，并结合[最终实施计划](../VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md)。

执行模式：按依赖连续实施，不逐项等待用户确认。实现、自动化检查、Windows VM/现场验收分别记录；生产 CloudBase、stable 发布、真实校区推送仍遵守计划授权门槛。

当前用户指示：持续完成 Maintenance 的代码与文档实现；测试任务后续统一安排。自 2026-11-03 本轮开始未运行测试、构建或安装；自动化、Windows VM、现场、发布状态分别保留为待验收，不因代码实施完成而标记通过。

## 当前基线

- 开始本轮时 HEAD：`5b5cf7306e99f0fc8dab176f893e360c549a025b`（`develop`）。
- 已形成的本地代码基线提交：`0090395 Implement Maintenance service foundation and workflows`（`develop`，领先 `origin/develop` 1 个提交；未推送）。
- 工作区原有用户输入：未跟踪的 URL 编码完整实施计划；保留原样，未加入代码提交。
- .NET SDK：需显式使用 `C:\Program Files\dotnet\dotnet.exe`（x64 SDK `10.0.401`）；PATH 中默认 `dotnet.exe` 指向 x86 且不能满足 `global.json`。

## 任务状态

| 顺序 | 任务 | 状态 | 证据 / 缺口 |
| ---: | --- | --- | --- |
| 01 | P0-01 更新待验证时禁止继续配置同步 | `FIXED-CODE` | 既有提交 `13124e9` 与结构化逐设备门禁；自动化证据见原计划。真实旧 Agent/Windows VM 仍 `NOT-TESTED`。 |
| 02 | P0-02 历史 Agent、签名与发行矩阵 | `BLOCKED-BY-ENVIRONMENT` | 缺 Windows VM 快照和独立 CloudBase 测试环境；production 迁移/写入未执行。按连续开发模式先继续独立代码项，未将本项标记通过。 |
| 03 | P0-03 / Issue 001 stable Agent 暂存版本 | `FIXED-CODE; VM-PENDING` | 内置 Agent 版本统一用于事务 rebasing 与 staging；`StageAgentUpdate` 另核对源程序集、目标目录和暂存程序集版本。已有定向夹具证据见原计划；Windows `.56` stable Schema 3 一跳升级仍未验证。 |
| 04 | P0-07 / Issue 007 ACK 与最终版本分层 | `UNIT-PASS; VM-PENDING` | 从 `v1.1.44` 源码确认 202 回执由旧 Agent 以源 Agent 版本签名。教师端新增 pre-switch ACK / synchronous result 分开验证；committed 只按签名身份、事务、Setup/Agent 目标和健康状态核验。定向检查通过，Windows 旧 Agent 更新仍未实测。 |
| 05 | P0-08 / Issue 008 未知 Agent 目标 | `CODE-HARDENED; PREVIOUS-UNIT-PASS; VM-PENDING` | unknown target 保持 nullable；Setup 已达标时明确 `ManualRequired`。本轮修正教师工作流过早拦截 null Agent target 的问题：当签名 Setup 仍低于候选时允许完整可信 StudentSetup 包进入 planner；后续仍以安装后真实 Agent 程序集读回和防降级门禁判定。当前 Agent 高于候选且 Setup 仍需更新时继续人工处理。历史版本矩阵夹具有通过记录；本轮未运行测试，v4/production API 与 Windows VM 未测。 |
| 06 | P0-04 / Issue 005 回滚与恢复闭环 | `CODE-HARDENED; UNIT-PASS; VM-PENDING` | 移动前核验旧 Setup/Agent 文件、版本、SYSTEM 任务、配置指纹和健康状态；恢复时核对 Agent 二进制版本；定向夹具通过。锁文件、权限、重启与 UpdateHelper 故障注入仍待 Windows VM。 |
| 07 | P0-05 / Issue 002 配置快照一致性 | `CODE-HARDENED; UNIT-PASS; VM-PENDING` | 更新下载后在共享机器锁内重读快照；同步写后校验磁盘/本机健康头指纹，信任根变化拒绝。快照兼容性定向夹具通过，期间并发注入仍待 Windows VM。 |
| 08 | P0-06 / Issue 003 课堂能力验收台账 | `LEDGER-ADDED; FIELD-PENDING` | 已逐项建账；仅用户曾确认的网站黑白名单完整链路为历史限定 `FIELD-PASS`，不代表当前版本；其余能力 `NOT-TESTED`。 |
| 09 | P1-01 设备状态机、身份、能力、计划和结果 | `MODEL-IMPLEMENTED; PREVIOUS-UNIT-PASS` | Core DTO 与状态机已有历史定向夹具；本轮已接入教师协调器与存储，详见 10–13。 |
| 10 | P1-02 MaintenanceCoordinator、旧 Agent adapter、journal | `CODE-IMPLEMENTED; TEST-DEFERRED; VM-PENDING` | Teacher Console 维护调用已进入 Coordinator；签名状态与更新/配置同步复用旧 Agent 通道；按校区/设备身份写入原子 JSON journal。需后续统一运行自动化与重启/并发 VM 验收。 |
| 11 | P1-03 ViewModel 薄入口与高级操作复用 | `CODE-IMPLEMENTED; TEST-DEFERRED` | Maintain、仅更新、仅同步配置、重新核对统一走 Workflow/Coordinator；移除了配置同步入口的无 journal 旧路径，并使软件更新入口不再绕过 Coordinator。仍需后续清理不再调用的旧 ViewModel 私有实现并做 UI 验收。 |
| 12 | P1-04 组件版本、事务与连接状态 | `CODE-IMPLEMENTED; TEST-DEFERRED; VM-PENDING` | 逐设备显示 Setup、运行 Agent、Agent 程序集、Maintenance、计划/签名读回版本、事务、认证课堂连接与核验时间；已签名读回优先显示。设备状态事故夹具和 UI 验收未运行。 |
| 13 | P1-05 教师连接失败诊断 | `CODE-IMPLEMENTED-PRELIMINARY; TEST-DEFERRED; VM-PENDING` | 新增本机受限诊断 sidecar、签名状态协议 v5 及 v4→v1 回退；诊断在本机分类，教师端通过既有签名 HTTP 状态读取；UI 提供故障码和下一步。需人工复审存储权限、协议兼容与故障分类，再做 VM 场景。 |
| 14 | P1-06 AppLocker/Store/Appx-MSIX（Issue 009） | `SUPPORT-SCOPE-CLARIFIED; VM-PENDING` | 教师端明确说明 AppLocker 只约束 EXE、Appx/MSIX 保持全签名放行；Store 隐藏/常规安装限制不等于阻止已安装游戏启动。规则和 Edition 的真实效果仍未在 Windows VM 验收。 |
| 15 | P2-01 独立 Maintenance Windows Service | `CODE-WIRED; TEST-DEFERRED; VM-PENDING` | 新增原生 SCM 服务宿主、LocalSystem 自动启动/失败重启与受限服务 DACL；StudentSetup 离线打包和 Inno 安装、更新停止/恢复、卸载接线已加入。尚未构建或在 Windows VM 安装验证。 |
| 16 | P2-02 Maintenance 协议、重放保护与独立身份 | `NETWORK-WIRED; TEST-DEFERRED; VM-PENDING` | 独立 SYSTEM 身份登记与 pin、持久重放窗、教师发起 WebSocket 通道、签名健康/能力/版本/配置同步/事务查询恢复已接入服务 host 和教师 adapter；未运行自动化或 Windows VM 验收。 |
| 17 | P2-03 复用 UpdateHelper 与既有事务 | `PARTIAL-CODE-WIRED; BUILD-AND-TEST-DEFERRED; VM-PENDING` | Teacher adapter 已按设备拆分 Maintenance/旧 Agent 通道，Maintenance 分支签发 LAN ticket、发送签名发行清单并分类事务回执；服务端完成 SYSTEM 私有暂存、发行签名/SHA/版本核验、UpdateHelper 交接和签名事务读回；事务日志使用跨进程互斥并拒绝覆盖活动事务。Maintenance 文件已移至 StudentSetup 目录外；服务控制端根据 SCM ImagePath 只迁移旧路径，迁移后的服务在常规 StudentSetup 更新期间保持运行。包下载/Helper 启动在切换前失败时记录 rolledBack；未知 Agent 目标要求 Agent 不低于源版本。首次旧服务迁移与卸载清理仍需 VM 验证；Maintenance 自身独立升级/回滚仍属 P3，尚未构建或执行自动化、Windows VM、现场验收。 |
| 18 | P2-04 StudentSetup 安装 Maintenance | `PACKAGE-WIRED; VM-PENDING` | StudentSetup 离线包包含独立服务文件；安装/更新时停止并重新注册服务，异常时尝试恢复旧服务；卸载前停止服务并在安全清理后移除 SCM 注册。ACL、升级覆盖、失败恢复和完整卸载均需后续 VM 验收。 |
| 19 | P3-01 Maintenance 自更新 | `WORKFLOW-WIRED; TEST-DEFERRED; VM-PENDING; FIELD-PENDING` | 已接入独立签名包接收器/事务、SYSTEM 私有暂存、版本化 Helper 安装切换/回滚/健康核验、教师端 artifact provider/adapter、Coordinator/journal 及 Workflow；高级维护操作新增“仅更新 Maintenance 服务”入口，和 StudentSetup 更新分开执行。发送前检查签名能力列表必须同时声明自更新和事务查询命令；旧服务未声明时改报人工引导升级，不盲发新命令。服务事务尚未与 StudentSetup 合并成自动继续的多阶段 operation。协议版本仍要求 v1 精确匹配；旧协议门槛协商、密钥轮换未完成。Migration 未部署；按用户要求未构建/测试/VM 验收。 |
| 20 | P3-02 看门狗、SCM 与重启恢复 | `PARTIAL-CODE; TEST-DEFERRED; VM-PENDING` | SCM 已配置 5/15/30 秒三次重启并按 24 小时重置；服务启动后会单次核对中断的 Maintenance 更新事务，验证 Setup/Agent/Service 版本后收敛为 committed 或需教师核对，并写入服务日志。损坏服务 EXE 无法启动时由独立 SYSTEM 恢复任务从快照回滚、任务退避/限次及跨阶段断电 VM 验收仍未完成。 |
| 21 | P3-03 完整故障注入/跨版本安全矩阵 | `NOT-IMPLEMENTED; TEST-DEFERRED; VM-PENDING` | 强杀、断电、文件占用、混合旧版本、回滚恢复与兼容拒绝矩阵未执行；VM 退出门槛保持。 |
| 22 | CM-01 Desired/Reported 与 revision | `PARTIAL-CODE; TEST-DEFERRED` | TeacherConnectivity revision、签名读回和 journal 已接通；其他配置模块尚未实施。 |
| 23 | CM-02 自动配置同步与离线对账 | `PARTIAL-CODE; TEST-DEFERRED` | Maintenance 签名同步现同时写 Agent 配置、维护 bootstrap，在共享更新锁内拒绝活动更新，重启 SYSTEM Agent 并等待活动配置指纹；运行失败回滚两侧快照，断电中断由受保护恢复记录在服务启动时恢复旧快照。教师 journal 新增按固定 Agent 身份持久化的 TeacherConnectivity 待发送队列；Agent 通道或独立 Maintenance 服务通道认证重连后会触发一次对账，按学生端健康状态签发下一 revision 并同步，重复连接事件有本机并发抑制。已完成源码接线，未构建/测试/VM 验收；CM-02 的其他配置模块与验证仍未完成。 |
| 24 | CM-03 Schema 迁移与回滚 | `PARTIAL-CODE; TEST-DEFERRED` | 修复 Schema 7 校区包归档器缺口；新增 Maintenance bootstrap Schema 1→2 显式迁移：保留原始字节快照、原子替换、核对 schema/revision/固定身份/配置指纹，失败时恢复原字节。Agent 本地主配置没有 schema 字段；校区包未来版本与 7→8→9 迁移语义尚未定义，未虚构目标 schema。未构建/测试/VM 验收。 |
| 25 | CM-04 无包首次安全配对 | `PARTIAL-CODE; TEST-DEFERRED` | Agent 首次身份由 SYSTEM 生成并通过签名状态披露；教师首次固定仍需显式勾选确认。新增学生课堂助手 12 位身份核对码，并在教师首次信任/身份轮换对话中并列显示，帮助现场核对设备。仅覆盖已装有签名校区公钥/证书配置的设备；限时无包入网、首次公钥/证书/公开配置安全分发、审计记录与 VM 验收仍未完成。 |
| 26 | CM-05 密钥轮换、配置状态与诊断 UI | `PARTIAL-CODE; TEST-DEFERRED` | TeacherConnectivity Desired/Reported 对账状态已进入逐台维护结果摘要；受控根密钥轮换、独立失败阶段/恢复建议视图仍未实现。 |
| 27–37 | P3–P5、LM 与候选发行任务 | `NOT-STARTED / DEPENDENCY-REVIEW` | 连续推进可执行代码/文档；测试、候选构建、隔离云、生产与现场门槛分开记录。 |

## 最近完成：P0-07 / Issue 007

**改动文件**

- `src/VeyonCampus.Core/StudentAgentIdentity.cs`
- `src/VeyonCampus.Core/StudentApplicationUpdates.cs`
- `tests/VeyonCampus.Checks/StudentApplicationUpdateChecks.cs`

**设计与结果**

- 新增 `ValidatePreSwitchAcknowledgement`，对 HTTP 202 回执绑定源 Agent 版本、目标 Setup 版本、CampusId、CommandId、签名时间窗与重启待完成标记。
- 新增 `ValidateSynchronousResult`；HTTP 200 按未重启时应运行的版本校验，避免把 unknown sidecar 当成目标相等。
- HTTP 202 后的 `committed` 读回不再要求最终 Agent 版本等于切换前回执版本；改由 committed 事务、目标 Setup/Agent 版本、固定身份及健康状态核验。
- 保留 200/202 状态与 payload 标记一致性、CampusId、CommandId、时间窗、版本格式及签名公钥校验。

**自动化检查**

- 命令：`& 'C:\Program Files\dotnet\dotnet.exe' run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-restore -p:NuGetAudit=false -p:UsedAvaloniaProducts= -- --student-application-update-fixtures`
- 结果：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures`。
- 备注：存在 NuGet vulnerability feed 网络警告 `NU1900` 和已有平台分析警告 `CA1416`；无 Windows VM 结果。

**剩余边界**：`0.4.55/0.4.56` 历史签名回执与新旧最终读回的 Windows VM 矩阵、错误身份/延迟/重放现场场景尚未执行。不能标记 `VM-PASS` 或 `FIELD-PASS`。

## 最近完成：P0-08 / Issue 008

**改动文件**

- `src/VeyonCampus.Core/StudentMaintenancePlanning.cs`
- `tests/VeyonCampus.Checks/StudentApplicationUpdateChecks.cs`

**设计与结果**

- Agent 目标版本未知用 `int?` 分支表达，不再把 `null` 当作版本相等。
- `StudentSetup` 已达目标或更高时，未知 Agent 目标返回 `ManualRequired`，原因明确说明 Agent 目标未知；不会显示无更新或同步配置。
- `StudentSetup` 落后时，仍允许沿受信任的完整安装包更新，后续由 P0-03 从安装器读回真实内置 Agent 版本并实施防降级；无目标的 release 不会伪造一个数字版本。
- 当 Agent 已高于签名候选但 Setup 仍落后时，计划现在先人工处理，避免安装候选包时覆盖已装的更高 Agent。若 Setup 已满足且 Agent 高于候选，只保留高版本并在原因中明确“不降级”。
- 增加 stable 与 local-test 两通道、Setup 低/等/高、Agent 低/等/高的规划矩阵，并覆盖 unknown target 的 Setup 低/等/高路径。

**自动化检查**

- 同 P0-07 定向命令：`StudentApplicationUpdateChecks` fixtures。
- 结果：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures`。
- 备注：仍有 `NU1900` 和既有 `CA1416` 警告。

**剩余边界**：v4 Agent target sidecar 在隔离 API 环境的签名/摘要联验，以及 0.4.56 Windows VM 的计划显示和更新结果仍未实测。

## 最近完成：P0-04 / Issue 005（代码与定向夹具）

**改动文件**

- `src/VeyonCampus.Core/WebsitePolicyRuntime.cs`
- `src/VeyonCampus.Core/StudentUpdateAtomicTransaction.cs`
- `tests/VeyonCampus.Checks/StudentApplicationUpdateChecks.cs`
- `docs/VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md`

**设计与结果**

- 移动旧 Student 安装目录前，先核验旧角色标记和 StudentSetup 程序集版本与事务记录一致。
- 同一移动前门禁核验旧 Agent 在固定版本目录中且程序集版本匹配、SYSTEM 任务仍指向该旧 Agent、校区配置指纹与事务快照相同，并通过健康接口读回。
- 完成目录移动后再次验证旧 StudentSetup 角色和程序集版本；失败时保留恢复材料供事务恢复。
- Agent 任务恢复和回滚路径校验版本目录名与实际程序集版本相同，拒绝错版二进制。
- 新增定向夹具：同版本 Agent 程序集通过、版本不符拒绝。

**自动化检查**

- 命令：`& 'C:\Program Files\dotnet\dotnet.exe' run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-restore -p:NuGetAudit=false -p:UsedAvaloniaProducts= -- --student-application-update-fixtures`
- 结果：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures`。
- 备注：NuGet 漏洞源网络警告 `NU1900` 及现有 `CA1416` 平台分析警告；未运行 Windows SYSTEM/VM 故障注入。

**剩余边界**：历史 Access Denied/锁文件、文件移动失败、安装器退出码、UpdateHelper 崩溃、重启中断和恢复失败熔断场景均须在 Windows VM 验收；未将代码夹具等同于事故复测。

## 最近完成：P0-05 / Issue 002（代码与定向夹具）

**改动文件**

- `src/VeyonCampus.Core/WebsitePolicyRuntime.cs`
- `src/VeyonCampus.Core/StudentApplicationUpdates.cs`
- `tests/VeyonCampus.Checks/StudentApplicationUpdateChecks.cs`
- `docs/VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md`

**设计与结果**

- 每次 Agent 请求继续从原子写入后的配置文件读取快照；更新在拿到共享机器锁时重新读取快照，允许签名配置同步在下载期间更新教师连接端点/TLS 根，并让后续事务使用新指纹。
- 更新绑定检查禁止请求排队期间校区、公钥、部署/应用/遥测身份或应用/系统策略信任根变化。
- 配置同步仍通过共享机器锁拒绝活动更新；写入后比较磁盘对象和配置指纹，绑定读回对象，并等待 `/health` 响应头返回相同配置指纹后再确认。
- 新增快照夹具验证连接端点轮换可用，网站/应用策略公钥或部署身份变化被拒绝。

**自动化检查**

- 命令同 P0-04 定向更新夹具。
- 结果：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures`。
- 备注：仍有 `NU1900` 与已有 `CA1416` 警告；未执行更新下载与配置同步重叠的 Windows 并发注入。

**剩余边界**：需在 Windows VM 验证配置同步恰好与下载/预检同时发生的重放序列、健康头刷新和拒绝策略，以及配置缺失时的诊断分类。

## 最近完成：P0-06 / Issue 003（验收台账）

- 新增 [课堂功能现场验收台账](classroom-feature-acceptance.md)，分别列出 Veyon 基础课堂控制、WebSocket、双向消息、课堂状态、网站策略解除、AppLocker、系统限制、Edge 策略、下课恢复及重启恢复。
- 仅记录用户已确认的历史网站黑白名单“下发→实际生效→到期自动解除”为限定场景 `FIELD-PASS`；构建版本、校区设备、学生账户和日期未知，明确不可作为当前版本证据。
- 所有没有满足完整现场条件的其他能力保持 `NOT-TESTED`；AppLocker 和系统限制标明先在可还原 VM 使用普通用户验证。
- 该项是台账和历史证据整理完成，不代表现场验收完成。P0-06 的现场能力实测仍待 VM/校区环境。

## 最近完成：P1-01（Maintenance Core 模型）

**新增文件**

- `src/VeyonCampus.Core/Maintenance/MaintenanceDeviceIdentity.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenanceCapabilities.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenancePlan.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenanceRun.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenanceResult.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenanceJournal.cs`
- `src/VeyonCampus.Core/Maintenance/MaintenanceValidation.cs`
- `tests/VeyonCampus.Checks/MaintenanceModelChecks.cs`

**结果**

- 稳定设备键为 `campusId + pinned Agent identity SHA-256`；当前 IP/主机名是可更新路由字段，不参与身份比较。
- 计划携带 Setup/Agent/Agent 程序集/Maintenance 版本 DTO、身份绑定的能力摘要、目标版本、发行/配置 SHA、Stable/LocalTest 通道与原因。
- run 记录有状态迁移时间、原因、Agent 命令 ID 和可审计签名摘要；已完成/健康状态必须绑定当前身份的证据。
- journal 同一设备仅允许一个活动 operation；重复 operationId 返回既有记录，状态持久化要求历史只追加、计划意图和命令 ID 不变。
- 新增状态图、IP 变更保持身份、第二 command 拒绝、活动操作互斥、JSON round-trip 等夹具。

**自动化检查**

- 定向更新夹具通过：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures`。
- 仍有环境原有 `NU1900` 与 `CA1416` 警告；无教师端网络或 VM 测试。

**边界**：本工作包定义和验证了类型/状态机，不含实际教师端 adapter、签名能力采集或本机 journal 文件原子写入；这些由 P1-02 接入。

## 2026-11-03 连续实施补记：P1-02 至 P1-05 代码

本轮按用户指示连续推进，未运行测试、构建、安装或 VM 验收。上表中的 `CODE-IMPLEMENTED` 只表示源码路径已接入，不代表自动化通过或实际设备通过。

- P1-02：增加教师侧 `TeacherMaintenanceCoordinator`、`LegacyAgentMaintenanceAdapter`、`MaintenanceRunStore` 和 `TeacherMaintenanceWorkflow`；已签名状态、下载能力、更新事务和配置同步终态写入可恢复的 operation journal。配置同步 ACK 不直接当完成，必须读回目标配置指纹、签名版本和健康事实。
- P1-03：更新、配置同步、全面维护和恢复查询走统一工作流；禁止在 Coordinator 不可用时退回到无 journal 的旧配置同步/更新入口。ViewModel 中旧的私有更新实现仍需后续清理。
- P1-04：逐台结果分开记录初始检查与更新后签名状态；能力接口与状态接口的版本/配置读回冲突时不判定成功，Agent 程序集版本在可签名读回时单独展示。
- P1-05：添加本机教师通道诊断状态、签名 v5 状态响应，并对旧 Agent 降级读 v4/v3/v2/v1；信息限于固定错误码、时间戳、配置指纹和安全文本，不发送到云端。教师端将“最近签名上报”与当前课堂长连接分开显示，并提供下一步排查提示。
- 后续核查：sidecar 的目录 ACL 继承与安装权限需 Windows VM 核验；v5→v4 兼容、旧 HTTP 诊断读回和断连/重连实际时间线未验证；AppLocker、独立 Windows 服务尚未在本轮完成。

## 2026-11-03 连续实施补记：P0-08 与 P2-02 身份/协议基座

本轮只进行了代码与文档修改；按用户要求未运行测试、构建、安装或 VM 验收。

- P0-08：教师维护工作流此前在规划前就拒绝所有未知 Agent 目标，即使签名 StudentSetup 候选高于当前 Setup 且包包含可信 Agent。现在允许这种完整包情况进入 planner；Setup 已达标时未知 Agent 仍是人工处理。实际 Agent 版本仍须在 Setup 执行后读回，不能由签名候选推断成功。
- P2-02：修复 Maintenance 协议设备 SHA-256 比较对小写指纹误拒的问题；验证请求与签名响应时先规范化指纹，再做固定时间外的身份一致性比较。
- P2-02：新增 `MaintenanceServiceIdentityStore`，将独立 Maintenance 私钥存放在 Agent 配置目录下独立 `Maintenance` 子目录，目录/文件 ACL 限制到 SYSTEM 和 Administrators；密钥文件先保护 ACL 再写入私钥。Maintenance 公钥登记由既有固定 Agent 身份签名，教师端验证登记时同时核对已固定 Agent 密钥、校区、设备指纹、Maintenance 公钥摘要和短时效。
- P2-02：新增 `MaintenanceServiceTrustStore`，只从已固定 Agent 密钥签名的登记凭据接受 Maintenance pin；Maintenance key 变更默认拒绝，显式传入轮换授权才替换。该 pin store 尚未接入教师端网络握手/设备状态 UI。
- P2-02：`AgentFileSecurity.SecureTree` 现在识别 Maintenance 子目录并保留其 SYSTEM/Administrators 私有 ACL，拒绝该目录内的链接与嵌套目录。
- 上述身份与协议基座后续已继续接入 P2-01/P2-02：SCM 服务 host、主动 WebSocket 命令通道、dispatcher、StudentSetup 服务安装/更新/卸载接线已写入源码；Maintenance 更新也已接入 Teacher adapter、SYSTEM 暂存、UpdateHelper 和事务读回。本轮继续接入 Coordinator/journal、教师 Workflow 与“仅更新 Maintenance 服务”界面入口。没有执行构建或测试，因此这些条目不得视为可安装或验收通过。
- P2-03 新增静态保护：服务进程与 SYSTEM UpdateHelper 通过跨进程命名互斥访问同一事务日志；新 commandId 不能覆盖 pending/inProgress/needsReview 事务；同 commandId 不能更改事务意图；committed/rolledBack 不能被迟到写入回退。下载/Helper 启动在安装器接管前失败时记录 rolledBack，允许下一次新 commandId 重试；无 signed Agent target 时，服务终态读回要求 Agent 版本不低于事务来源版本。该保护尚无自动化或 Windows VM 证据。
- P2-03 服务路径调整：安装器现在把服务文件安装到 `{autopf}\Veyon Campus\MaintenanceService\Current`，从 StudentSetup `{app}` 递归文件安装中排除服务目录；首次安装创建服务，旧 SCM ImagePath 位于 StudentSetup 时执行一次迁移，已迁移的独立服务不在常规 StudentSetup 升级中替换或停止。UpdateHelper 不再主动停止服务。此实现仍是源码级接线，需 VM 证实旧路径迁移、并发服务进程、目录 ACL、升级失败和卸载行为；服务自身的版本升级/回滚仍需 P3-01。
- LM-01 路由保护：Legacy adapter 现在区分“无 Maintenance 注册/通道”与“Maintenance 身份已固定但离线”或“存在未固定通道”。后两种转为 `ManualRequired`，不再静默回退到 Legacy；更新批次在发送前将这类设备排除，避免因超时或信任异常并行走第二条更新路径。该分支尚无自动化或现场验收证据。
- CM-01 revision/journal 接线进行中：Maintenance 请求要求同步命令带正数 revision；SYSTEM bootstrap schema 2 持久化 revision，只接受严格 `current + 1`，签名 health 与同步回执都返回 revision。教师 adapter 读取 health 再发下一 revision，并核对回执。初次成功和恢复成功均将 Desired/Reported 写入 Schema 3 Teacher journal；恢复读回 revision 必须与既有 Desired 一致，同 revision 的签名报告只允许内容相同、采集时间更新的刷新。Desired signer fingerprint 来自本机教师签名公钥，信封摘要来自发送前持久化的签名命令摘要。界面尚未展示；schema 1 bootstrap 读取兼容。未构建/测试，CM-01 仍进行中。
- CM-05 状态判定基础：新增配置对账状态计算，区分无 Desired、等待签名 Reported、Pending、Rejected、Drifted、InSync，并暴露 Desired/Reported revision、指纹及最后 Reported 时间；`IsConfigurationInSync` 改用完整状态判断。后续接入维护结果摘要，见下条；未构建/测试。
- CM-05 教师结果摘要接线：逐台维护和事务恢复结果追加 TeacherConnectivity 对账状态、Desired/Reported revision 与最近签名读回时间；不会把本地 journal 中没有签名 Reported 证据的记录标成 InSync。其他配置模块、可操作的失败阶段/建议 UI 尚未覆盖；未构建/测试。
- CM-03 Schema 7 归档修复：`CampusConfigurationArchive` 现允许 Schema 7，归档/解包固定文件清单纳入 `teacherChannel` TLS `.cer`，最大文件数从 7 调至 8；解析时验证 1–16 个教师端点、只允许根目录证书文件，并要求 files 清单与实际固定文件一致。Schema 3–6 原校验保留。此改动未运行构建或测试；Schema 7 包创建→上传归档→下载解包、重复字段/证书路径篡改拒绝均待后续测试。
- CM-03 Maintenance bootstrap 本地迁移：服务读取既有 Schema 1 bootstrap 后，先按旧字段集合和当前 Agent/教师信任身份完整验证，再原子写入 Schema 2（revision 0），读回核验固定身份、配置指纹和 revision；写入/读回失败时用操作前原始字节快照恢复。当前仅落地这个有明确旧/新版本的模块迁移；Agent 主配置本身没有 schema 字段，校区包的 8/9 结构和迁移目标没有定义，因此未创建猜测性的通用 7→8→9 迁移。未构建/测试/VM 验收。
- CM-04 首次身份现场确认基础：Student Companion 现从本机 SYSTEM Agent 的签名课堂状态读取其公钥指纹，显示分组的 12 位配对核对码；教师首次信任/身份变化窗口显示同一核对码并要求明确勾选现场设备名称/位置和核对码已核实，再由既有信任库固定身份。签名验证仍在短码比较前执行，IP/路由地址不会自动成为信任依据。此实现仅帮助已配置教师信任根的部署现场确认设备；没有实现无包的新设备 bootstrap、限时配对邀请或首次分发教师公钥/TLS 根，不能视为 CM-04 完成。未构建/测试/VM 验收。
- P3-01 Maintenance 自更新现状与差距：现有运行时更新执行器仍只能下载 StudentSetup 发行包，借用 StudentSetup UpdateHelper 和 Inno 覆盖同包内服务文件；未消费新 MaintenanceService 发行角色。源码已新增独立 Maintenance 项目版本（当前 1.0.0）、schema 5 签名发行角色、v5 latest API/RPC 源码、独立 `Versions/<service-version>` 安装目录、Maintenance 单独 Inno 脚本与 `scripts/package-maintenance-service.ps1` 打包入口。StudentSetup 内嵌服务也改为使用自身版本和版本目录，常规 StudentSetup 升级保留外部独立服务。安装器/打包入口尚未构建；服务端 migration 未部署；Updater、专用 SYSTEM Helper、安装后独立版本健康读回、失败回滚和密钥双信任轮换仍未实现。代码实施 `PARTIAL-CODE`；`TEST-DEFERRED; VM-PENDING; FIELD-PENDING`。
- P3-01 本轮静态修正：独立安装器在切换 SCM 前，改为从旧 SCM ImagePath 指向的已知版本执行查询与停止，避免新目标版本尚未安装时调用不存在的目标 EXE；未知路径或旧文件缺失时停止安装并保留现场。打包入口增加 artifacts 路径逐级重解析点检查、输出相互包含检查、runtimeconfig 单一 `Microsoft.NETCore.App` 验证；Inno include 路径直接使用 Windows 路径，第三方声明脚本失败按 PowerShell terminating error 处理。以上仅源码审阅，未编译 Inno/运行 PowerShell 打包脚本。
- P3-01 StudentSetup 集成静态修正：服务已独立版本化后，StudentSetup 控制服务的命令现在从 SCM `ImagePath` 选择受管旧版本 EXE，并核对文件名及允许的版本根目录；普通应用升级保留服务在线，安装后启动/卸载读回复用登记路径。这个旧服务路径场景尚未编译或在 Windows 上验证。
- P3-01 独立 Helper 接线：Maintenance 单独打包源码现在把版本化 `VeyonCampus.UpdateHelper.exe` 与服务一起安装；服务优先启动自身版本目录的 Helper，兼容迁移期才回退到 StudentSetup 版本 Helper。Helper 在 StudentSetup 迁移事务中兼容当前服务/旧 Setup Helper 两种受管位置；Maintenance 专属更新则仅接受同版本服务目录内的 Helper。
- P3-01 独立发行事务实现增量：Maintenance 协议新增专属 `offerSignedMaintenanceServiceUpdate` / `getMaintenanceServiceUpdateStatus`，接收端只接受 schema 5 签名角色、严格匹配当前维护协议、SYSTEM 私有暂存和固定 LAN URL；独立事务日志按 commandId 幂等。版本化 Helper 运行 Maintenance 专属 Inno 包，先切换 SCM，再核对 SCM ImagePath、目标文件版本、服务运行态和 SYSTEM 配置身份；失败时由旧版本服务 EXE 切回旧 SCM 路径，失败现场保留并标 needsReview。教师端发行 provider 和单设备 adapter 方法已写入，但尚未由 Coordinator/journal/UI 调用，未完成多阶段 operation 串行与自动续跑 StudentSetup 更新。
- 独立发行元数据第一步：`ApplicationReleaseRole` 新增 `MaintenanceService`，Core 发布清单 verifier 增加 schema 5，签名规范载荷绑定独立产品/文件名/`maintenanceProtocolVersion`，并拒绝把 schema 5 用于 TeacherConsole/StudentSetup，或把旧 schema 用于 Maintenance。通用 Teacher/Student `Start` handoff 明确拒绝 Maintenance 角色，避免误用应用更新器。新增 v5 latest API response、MaintenanceService 发布脚本分支、固定 GitCode 文件名映射与 CloudBase migration/RPC 源码；这些服务端源码没有部署或执行。独立安装器和 SYSTEM 更新执行器还未消费该发行类型，现阶段不能安装 Maintenance 专属包。
- P3-02 恢复接线增量：SCM 既有失败动作已限制为 5/15/30 秒三次自动重启、24 小时重置；Maintenance Worker 现在在教师通道启动前单次读取未终结更新事务。仍运行的 SYSTEM UpdateHelper 会保留 inProgress；Helper 不在时，仅读回 StudentSetup、Agent 和当前 Service 版本都达到事务目标才提交，否则事务转人工核对。恢复决定和异常类型写服务日志，不覆盖或清理快照。若服务 EXE 自身损坏到无法启动，仍没有独立 SYSTEM 计划任务/静态恢复宿主来用快照替换它；限次退避和断电/强杀阶段矩阵也待后续实现与 Windows VM 验收。
- CM-02 教师连接配置双写及离线重发：Maintenance 接收一条已验签命令后，在 StudentUpdate 原子锁内确认无活动更新，只修改 Agent 的 endpoints/TLS root 和独立 Maintenance bootstrap；保留校区身份、签名公钥、策略公钥等不可变字段。写入前先持久化 SYSTEM/Administrators 私有恢复记录，含设备 pin、目标 revision/fingerprint、原始 Agent 配置和原 bootstrap；Agent 与服务配置写完并重启健康读回后删除记录。运行时失败会恢复两侧旧快照；若断电/强杀留有记录，服务启动会等更新事务结束后恢复旧快照，再开放维护通道。签名同步回执带 Agent 配置指纹，教师端检查其格式。Teacher journal 现按固定 Agent 身份保存离线 TeacherConnectivity 目标；Agent 或 Maintenance WebSocket 经认证建立后触发重连对账，执行前确认无活动维护事务，revision 仍从签名健康读回产生。已补齐源码级自动重发接线；尚未证明实际重连、跨进程队列恢复或并发竞争，且只覆盖教师连接模块。未构建/测试/VM 验收。
- 当前验证边界：本轮尚未构建安装包，用户现在没有可据此测试的 StudentSetup 或 Maintenance Service 安装包。完成代码实施后，仍需统一执行自动化检查与 Windows VM 安装/升级/回滚验收；随后才能打包给用户手动测试。Production 发布和真实校区推送仍是单独门槛。
- P1-06 / Issue 009：`TeacherPolicyView.axaml` 已明确区分 EXE 运行限制、Store 可见/常规安装限制与已安装 Appx/MSIX 启动限制；不再暗示当前 AppLocker XML 能限制已安装游戏。没有变更 Appx 放行基线，也没有将范围说明当作现场验收；Edition 支持矩阵、实际 Store 下载/启动、策略撤销与系统必需包保护均 `VM-PENDING` / `NOT-TESTED`。
