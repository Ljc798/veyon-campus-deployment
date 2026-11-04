# Veyon Campus — Maintenance 完整实施计划（Codex 连续开发版）

> 修订日期：2026-11-03  
> 远端核对基线：`Ljc798/veyon-campus-deployment`，`develop@5b5cf7306e99f0fc8dab176f893e360c549a025b`。  
> 关联原文件：`docs/VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md`（原文 833 行）。  
> **交付性质：可直接交给 Codex 的完整修订执行稿。本文保留原 P0–P4 与 Issues 001–009 的任务语义，并扩展至 001–012、Legacy 迁移、Configuration Management 和候选发行。不是已实现或已通过实机验收的声明。**

## 0. 给 Codex 的总执行指令

**RUN-TO-COMPLETION**：按本计划依赖顺序连续实施所有可执行任务。每个任务必须独立设计、编码、测试、提交、更新进度；**不必每完成一项就等待用户说“继续”**。但不得将全部修改塞进不可审查的单次提交。

**当前执行指示覆盖测试安排**：用户要求先持续完成 Maintenance 实现，测试任务稍后统一安排。因此在用户安排测试前，不运行测试、构建、安装或 VM 验收；继续做源码审查、代码和文档实现，并把未验证项标为 `TEST-DEFERRED` / `VM-PENDING`。依赖 Windows VM、隔离云环境或签名环境的验收留待后续，期间继续不依赖该环境的工作。

开始前检查 `git status`、`git rev-parse HEAD`、最近提交、当前代码和原始问题台账。不要覆盖未提交改动。原文件中的 `930af1a` 仅为历史审查基线；以执行时 HEAD 为准。先确认已有 P0-01、Issue 001 等是否已修复，禁止重复改坏。

**安全与发布授权**：可以开发、构建、测试、生成候选制品；**不得**未经明确批准修改生产 CloudBase、执行生产 SQL、覆盖 stable 发布目录、使用未经授权的私钥、自动向真实校区推送、关闭验签或强制跳过 Windows 恢复保护。自动测试成功不等于 Windows VM/真实校区已验收。

**最终期望**：完成 Teacher Console、Teacher MaintenanceCoordinator、Student Maintenance Service、Student Agent、Companion、StudentSetup、UpdateHelper 的完整维护架构；产出正式构建配置的 Teacher/Student **Release Candidate（RC）** 安装包及哈希、签名清单、变更日志、Windows 验收清单。签名或构建环境不满足时必须明确 `SIGNING-BLOCKED` / `BUILD-BLOCKED`，不能冒称正式稳定版。

## 1. 架构与职责冻结

| 组件 | 主要职责 | 禁止混入 |
| --- | --- | --- |
| Teacher Console | 设备/课堂管理，网站、应用、系统策略，消息与操作 UI | 直接替换学生机系统文件 |
| Teacher MaintenanceCoordinator | 选择维护通道、发起受限任务、持久化 operationId、展示结果；教师端自身更新调度 | 执行远端任意 Shell |
| Teacher UpdateHelper | 独立切换/回滚 Teacher Console 安装 | 依赖正在关闭的教师主进程 |
| Student Maintenance | 独立 SYSTEM Windows Service：可信更新、配置、恢复、健康与诊断 | 课堂策略引擎、普通聊天 UI、任意远程执行 |
| Student Agent | 接收认证的课堂指令、执行网站/系统/应用策略、反馈真实执行结果 | 软件安装器、维护协议唯一入口 |
| Student Companion | 学生会话 UI：消息、求助、通知、倒计时 | SYSTEM 权限、教师私钥 |
| StudentSetup | 首次安装、注册服务、安全配对、离线修复、卸载 | 常驻课堂业务 |
| Student UpdateHelper | 安装事务切换、版本目录与服务恢复 | 依赖被停止的 Agent/Maintenance 进程 |

**两条独立通道**：
- 课堂：Teacher Console ↔ Student Agent ↔ 本机 Companion IPC。
- 维护：Teacher MaintenanceCoordinator ↔ Student Maintenance ↔ UpdateHelper。维护可复用现有教师 TLS 监听基础设施，但须有独立路由、认证、会话与重连，不依赖 Agent WebSocket。

## 2. 旧版迁移：一次性跨越旧架构

### LM-01：维护路由选择

按**可信运行状态**而非 Agent 版本号判断：

1. Maintenance Service 身份、版本、协议能力、认证健康均可验证：走 Maintenance。
2. 没有 Maintenance，但 Legacy Agent 的可信签名更新入口可用：走 Legacy Signed Update。
3. 两者不可用、身份异常或事务 `recoveryRequired`：进入 `ManualRecovery`，提供现场修复说明。

维护超时不能盲目 fallback，避免两条执行路径同时修改同一设备。教师端设备级互斥、持久化 operationId 和幂等检查必需。

### LM-02：0.4.55 / 0.4.56 一跳安装 Maintenance

**禁止给旧 Agent 直接发送它不认识的新 Maintenance 协议或未经验证的 Updater EXE。**

兼容链路应为：

`Teacher Console → 旧 Agent 39174 /v1/update → 旧版认可的 stable Schema 3 可信发行清单 → 完整新版 StudentSetup → 注册 Student Maintenance → 认证健康读回 → 提交或恢复`

- 重点使用**真实公开版 StudentSetup 1.1.44 / Agent 0.4.56** 做 VM 起始基线，并单独验证 0.4.55。
- 不要求先升至 0.4.57；能直接一跳则直接迁移。
- 旧 Agent 不认识 `local-test` Schema 4 或新测试根时必须拒绝；不能绕过签名。
- 本地测试候选包可以通过 LAN 传输，不必公开发布；但旧 Agent 迁移必须使用**其原本信任的 stable 签名格式及密钥**。缺少合法签名能力时记录 BLOCKED。
- 不得把更新包被接受当成安装成功。必须读回 SCM 服务、SYSTEM 运行身份、二进制路径/版本、设备身份、Maintenance 协议和 Agent 运行状态。
- 迁移失败应保留旧 Agent/校区配置、回滚材料与事务日志；不可恢复时报告 `recoveryRequired`，不得报成功。

### LM-03：长期保留受限 Legacy 桥接

冻结历史 `/v1/update` 的协议和签名信任边界，仅支持经过真实验证的旧版迁移和必要安全修复，不再新增课堂业务能力。未知旧版本明确 `Unsupported` 或 `ManualRequired`。

## 3. MaintenanceProtocol.v1 与事务模型

### 3.1 协议最低能力

- `GetIdentity`
- `GetCapabilities`
- `GetMaintenanceStatus`
- `GetOperationStatus`
- `PlanSignedUpdate`
- `ApplySignedUpdate`
- `RecoverTransaction`
- `GetConfigurationStatus`
- `ApplySignedConfiguration`
- `GetDiagnostics`

命令绑定 CampusId、DeviceId、角色、operationId、协议版本、签名用途、目标版本、SHA-256、有效期、nonce。使用固定信任根、认证设备身份、重放防护和权限白名单。禁止任意 URL/命令/安装路径。兼容协议 v1 不应随着 Agent 课堂协议改变而破坏。

### 3.2 更新状态机

`Inspect → IdentityAndCapabilities → Planned → Downloading → Staged → ActivationPending → PendingVerification → Committed`

异常分支：`RolledBack`、`RecoveryRequired`、`ManualRequired`、`Rejected`、`Unknown`。

`202 Accepted` 只代表旧进程接受命令：按**源版本、身份、commandId、签名**核验；`Committed` 才按**目标版本、真实二进制、安装角色、健康状态**核验。两者不能强行比较成同一个版本。教师端重启后须能用 operationId 读回；任何 `PendingVerification` 不得继续配置写入。

### 3.3 安装目录与自更新

- Student Maintenance 安装为独立自动启动 SYSTEM Windows Service。
- 程序文件版本化放在 `Program Files` 独立目录；设备配置、事务日志、恢复材料放在受 ACL 保护的 `ProgramData`。
- StudentSetup 注册/修复/卸载 Maintenance；不能因更新 StudentSetup 停掉唯一恢复通道。
- Maintenance 自更新必须由**独立 UpdateHelper 或恢复任务**完成：停止服务、验签、切换版本、启动健康检查、提交；失败回旧版本或明确待恢复。
- 断电、进程强杀、目录占用、教师离线、同机多教师并发、旧 Agent 已停止都要纳入测试。

## 4. Configuration Management（CM-01～CM-05）

### CM-01：期望配置与报告配置

建立 `DesiredConfiguration`（教师端权威期望）与 `ReportedConfiguration`（学生端可信读回），包含模块、schemaVersion、revision、fingerprint、设备身份、签名与应用状态。区分：
- `CampusIdentity`：固定校区身份/信任根。
- `TeacherConnectivity`：地址、端口、TLS 根。
- `PolicyTrust`：受控签名密钥轮换。
- `SystemConfiguration`：系统/应用策略。
- `RuntimeSettings`：运行参数。

### CM-02：无配置包的日常同步

**已绑定、已信任设备**的教师地址、TLS、策略参数改变后，Teacher Console 更新 Desired，Student Maintenance 接收签名修订并应用，返回 Reported revision/fingerprint；重启仍一致。设备离线后重新连接时重新签发有效任务，不能重放过期命令。

复用现有 `StudentAgentConfigurationSync.cs`、`WebsitePolicyRuntime.cs` 和原子配置写入能力，避免造第二套冲突配置源。软件更新与配置同步同机串行，不能在切换中读到混合快照。

### CM-03：Schema 自动迁移与回滚

实现明确的模块级迁移器（例如配置 Schema 7→8→9），记录输入/输出 schema、revision、校验摘要。先快照，迁移后校验并热重载或重启，失败恢复。未知必需字段或不支持的版本要明确拒绝，不得默认当成“已最新”。

注意区分**校区配置包 schema**、**Agent 本地配置 schema**、**stable 发行 Schema 3**、**local-test 发行 Schema 4**和**Maintenance 协议版本**，不能混为一个数字。修复 `PackageBuilder` 支持 Schema 7 而 `CampusConfigurationArchive` 仅支持 3–6 的边界问题。

### CM-04：无包首次安全配对

全新 StudentSetup 安装后生成设备身份及限时配对会话；教师确认设备/短码或二维码、明确批准校区绑定，再分发所需**公钥、证书和公开配置**。禁止发送教师私钥，禁止仅靠局域网 IP 自动认领。校区转移、重绑定与密钥轮换须单独授权和审计。

### CM-05：对账、轮换与故障显示

教师端显示 Desired/Reported revision、配置漂移、最后成功时间、失败阶段、人工恢复建议。受控根密钥轮换必须由旧可信根授权新根并经过过渡期；无信任证明不自动接受新证书。

## 5. 原 Issues 001–012 的修复和验收

| ID | 问题 | 必须达到的验收条件 |
| --- | --- | --- |
| 001 | stable 更新暂存使用过时 Agent 目标版本 | 安装包 Agent、staging、Helper、journal、真实运行版本一致 |
| 002 | 配置同步后存在旧快照/指纹不一致 | 内存/磁盘/HTTP/维护健康与重启后的配置指纹一致 |
| 003 | 课堂与系统策略缺真实 E2E | 每项按 `FIELD-PASS/FAIL/NOT-TESTED` 独立记录，网站黑白名单与到期解除强制回归 |
| 004 | “最新”掩盖 Setup/Agent/恢复状态 | 分开展示 Setup、运行 Agent、Maintenance、事务和课堂连接 |
| 005 | 更新失败且回滚失败 | 文件占用、路径丢失、断电后可恢复或明确 `RecoveryRequired` |
| 006 | 教师连接诊断不完整 | 区分缺地址、缺证书、TCP、TLS、认证、协议、未监听、离线 |
| 007 | 切换前 ACK 用目标版本校验 | 源版本 ACK 与目标版本 committed 分阶段核验 |
| 008 | 未知 Agent 目标误当作无须更新 | `TargetUnknown` / `ManualRequired`，绝不静默跳过 |
| 009 | AppLocker Appx/MSIX/Store 覆盖缺口 | 按 Windows Edition/普通用户真实阻断、解除恢复；不能全放行 Appx |
| 010 | Agent 运行但 SYSTEM 启动/恢复任务缺失 | 任务注册、账户、路径、重启恢复独立读回，不能只看 health 200 |
| 011 | 公开 EXE 缺 Authenticode 签名 | 真实代码签名状态、最终 EXE SHA、发行清单签名分别核验 |
| 012 | CloudBase 迁移冲突 | allowlist 迁移离线验证与隔离环境测试；禁止直接碰生产 |

以上问题保留原始证据状态：源码疑点不等于 VM 失败，已提交修复不等于真实修复验收。每条记录 commit、复现条件、测试结果和证据。

## 6. 连续执行任务表（按依赖推进）

| 顺序 | 任务 | 交付 |
| ---: | --- | --- |
| 01 | P0-01 | 更新 PendingVerification 时禁止继续配置同步 |
| 02 | P0-02 | 真实旧 Agent/签名/安装包兼容矩阵 |
| 03 | P0-03 / 001 | stable Agent 目标版本与暂存一致 |
| 04 | P0-07 / 007 | ACK 与最终版本分层校验 |
| 05 | P0-08 / 008 | 未知目标版本安全处理 |
| 06 | P0-04 / 005 | 原子回滚和恢复故障注入 |
| 07 | P0-05 / 002 | 配置快照和健康指纹一致性 |
| 08 | P0-06 / 003 | 课堂 E2E 基线和网站策略回归 |
| 09 | P1-01 | 设备状态机、能力和版本 DTO |
| 10 | P1-02 | MaintenanceCoordinator 和持久化 journal |
| 11 | P1-03 | Thin ViewModel，复用维护服务 |
| 12 | P1-04 / 004 | 独立组件版本/事务/连接 UI |
| 13 | P1-05 / 006/010 | 连接与计划任务诊断 |
| 14 | P1-06 / 009 | AppLocker/Store/Appx 精准控制 |
| 15 | P2-01 | 独立 Student Maintenance SYSTEM Service |
| 16 | P2-02 | 维护协议 v1、认证与独立通道 |
| 17 | P2-03 | UpdateHelper、更新事务与恢复复用 |
| 18 | P2-04 | StudentSetup 安装、注册、卸载 Maintenance |
| 19 | LM-01 | Legacy/Maintenance/Manual 路由选择 |
| 20 | LM-02 | 公开版 0.4.56 → 新版 Maintenance 一跳升级 |
| 21 | LM-03 | 0.4.55 及更旧版本兼容/拒绝矩阵 |
| 22 | CM-01 | Desired/Reported 和配置 revision |
| 23 | CM-02 | 自动配置同步和离线对账 |
| 24 | CM-03 | Schema 迁移与配置事务回滚 |
| 25 | CM-04 | 无包首次安全配对 |
| 26 | CM-05 | 密钥轮换、配置状态与诊断 UI |
| 27 | P3-01 | Maintenance 自更新 |
| 28 | P3-02 | 看门狗、SCM 与重启恢复 |
| 29 | P3-03 | 完整故障注入/跨版本安全矩阵 |
| 30 | P4-01 | 教师主界面精简为设备概览、学生端维护 |
| 31 | P4-03 | 设备健康与诊断中心 |
| 32 | P4-04 | 网站规则、系统策略、双向消息真实回归 |
| 33 | P5-01 | CI、API 契约、Windows installers 全部检查 |
| 34 | P5-02 / 011 | 候选安装器、代码签名政策、SHA、release sidecar |
| 35 | P5-03 / 012 | 隔离 CloudBase 迁移与发行 API 验证 |
| 36 | P5-04 | 非 stable RC 发布/候选包交付 |
| 37 | P5-05 | 用户 Windows VM 和现场逐项验收清单 |

任务依赖不满足时，记录 `BLOCKED-BY-ENVIRONMENT`，继续**不依赖**的工作，不允许跳过签名、事务或安全测试来制造“全部完成”。

## 7. 测试矩阵（必须提供可复现脚本）

### 7.1 历史升级与事务
- T01：真实 0.4.55 → 新版含 Maintenance 的 Setup。
- T02：真实公开版 1.1.44 / Agent 0.4.56 → 新版。
- T03：旧 Agent 收到不可信 local-test Schema 4 → 拒绝且不修改安装。
- T04：稳定版 Schema 3 内置 Agent 目标比当前高 → staging/运行一致。
- T05：202 ACK 是旧版本、Committed 是新版本 → 正确校验。
- T06：未知 Agent 目标 → 不误判“已最新”。
- T07：Setup 相同但 Agent 更旧 → 能识别实际更新需要。
- T08：目录占用、Access denied、缺失旧 Agent 恢复路径 → 回滚或明确 RecoveryRequired。
- T09：教师关闭、回执丢失、断电、网络中断 → 幂等与事务读回。
- T10：两教师并发、10–70 台批量、离线重连 → 互斥/限流/故障隔离。

### 7.2 独立维护与配置
- T11：Agent 停止，Maintenance 仍能认证、更新和恢复。
- T12：Maintenance 自更新被强杀或断电 → Helper 恢复旧版。
- T13：首次安装/卸载/重启，SCM、SYSTEM、ACL、服务路径正确。
- T14：Companion/Agent/Setup 更新时维护服务持续可用。
- T15：教师地址和 TLS 变化，无需重新生成包，revision/fingerprint 读回一致。
- T16：配置 Schema 7→8→9，失败快照恢复。
- T17：配置更新与软件升级并发 → 串行化、无混合配置。
- T18：设备离线再上线 → 只应用最新合法修订。
- T19：无包首次配对、冒名、重放、跨校区绑定 → 授权正确或拒绝。
- T20：受控根密钥轮换、未知根 → 合法过渡或拒绝。

### 7.3 课堂、系统与发布
- T21：网站黑白名单生效及到期自动解除（现有已验证行为不得回归）。
- T22：Veyon 基础控制在维护升级后不回归。
- T23：课堂双向消息/Companion UI、断线恢复、权限。
- T24：AppLocker/Store/Appx/MSIX 在支持的 Windows Edition 上实际阻断、到期恢复。
- T25：Agent SYSTEM 任务缺失时报告真实安装健康。
- T26：缺地址、缺根证书、TCP/TLS/认证/协议异常诊断。
- T27：正式 RC EXE、发行签名、SHA-256、Agent 目标 sidecar 一致。
- T28：隔离云环境迁移、v3/v4 兼容、未触碰 production。

所有结果必须标记 `UNIT-PASS` / `CI-PASS` / `VM-PASS` / `FIELD-PASS` / `FAIL` / `NOT-TESTED` / `BLOCKED`，不能把前两者当作实机通过。

## 8. CI、签名与正式候选包交付

1. 运行仓库 App checks、API contract checks、Windows installers；修复失败，不得通过注释测试绕过。
2. 生成 Teacher Console 和 StudentSetup（包含 Maintenance、Agent、Companion、Helper）**同一源码 SHA** 的 Windows 安装器。
3. Authenticode 与发行清单 RSA 签名是两回事：若有获授权代码签名证书，**先签最终 EXE**，再计算 SHA-256 并生成发行签名清单及 Agent sidecar；无证书则明确披露未 Authenticode 签名，不得伪造。
4. Legacy 0.4.55/0.4.56 迁移必须使用其已信任的 stable Schema 3 与合法发行签名。若缺正式 Developer Release 私钥，记录 `SIGNING-BLOCKED`，不能为了测试降低验签。
5. 可以创建**非 stable、非生产目录的 RC/pre-release**，包含安装包、`.release.json`、sidecar、SHA256SUMS、变更日志、构建 SHA、测试矩阵；发布权限或门禁不满足时交付本地构建产物及阻塞原因。
6. CloudBase 的 allowlist 迁移必须先离线验证，再在**隔离测试环境**经授权执行。不得默认改生产库或覆盖 stable `/v4/releases/latest`。
7. 用户完成 Windows VM 与非关键校区验收并明确授权之后，才允许晋级 stable 和 Ring 0→3 灰度。

## 9. 最终交付清单（Definition of Done）

- [ ] P0–P4、LM-01～03、CM-01～05、P5-01～05 全部有代码提交或明确阻塞记录。
- [ ] 001–012 台账每条有修复 commit、测试、状态和证据。
- [ ] 0.4.55/0.4.56 历史真实升级验证或准确标记签名/环境阻塞。
- [ ] Maintenance 独立于 Agent，能够自更新、修复和恢复；Setup 安装、卸载、恢复正常。
- [ ] 配置无需反复生成包，Schema 迁移、配置对账、无包安全配对可用。
- [ ] 教师端版本、连接、配置、事务状态真实可读；网站规则和 Veyon 无回归。
- [ ] Windows Installer / API / App checks 的真实结果和 Windows VM 结果分开披露。
- [ ] Teacher/Student 候选安装包、签名状态、哈希、源码 SHA、逐项验收文档齐备。
- [ ] 未经用户验收不宣布 stable，不修改生产 CloudBase，不批量推送校区。

## 10. 可直接复制给 Codex 的总 Prompt

```text
请执行 docs/VeyonCampus_Maintenance_完整实施计划_Codex连续开发版.md。

采用 RUN-TO-COMPLETION 模式：从当前 git HEAD 和工作区开始，阅读原
docs/VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md
以及现有问题台账 001–012、maintenance 文档和已有测试。
不要覆盖未提交改动，也不要重复实现已修复的任务。

按计划依赖顺序连续完成 P0–P4、LM、CM 和 P5 所有可执行工作，
每个工作包先复核故障机制，再实现并更新
docs/maintenance/implementation-progress.md；无需每项等待我说继续。
用户安排测试前，不运行测试或构建，明确记录为未验证，并继续推进其他代码和文档项。
遇到缺 Windows VM、正式签名凭据或隔离云环境时如实标记 BLOCKED，
继续不依赖的开发，但不得伪造通过结果或降低安全验证。

必须实现：旧 0.4.55/0.4.56 通过其已认可的 stable Schema 3 签名
更新链路一跳安装含独立 Maintenance 的 StudentSetup；后续使用
独立 Maintenance 维护 Agent/Companion/Setup/Maintenance；
教师端 MaintenanceCoordinator 统一调度；配置自动同步、Schema 迁移、
无包首次安全配对；保留网站策略生效和到期解除。

完成后构建 Teacher/Student 正式目标 Release Candidate，输出
安装包、发行签名清单、SHA256SUMS、真实 CI/VM 结果和逐项实机验收表。
有权限且门禁通过可创建明确非 stable 的预发布；否则记录阻塞和构建
产物位置。不要未经我批准修改生产 CloudBase、覆盖 stable、
向真实校区批量推送或宣称正式版已经通过实机验收。
```

---

**备注：** 原远端计划中的历史问题证据、原 T01–T40 明细及既有开发记录仍是执行时的参考依据；本稿是完整执行修订版，不能替代对仓库实时状态的复核。
