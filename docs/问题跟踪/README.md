# 项目问题记录

更新日期：2026-11-03。审查基线：`930af1a`；包含 P0-01 代码修正的当前提交：`13124e9`。

本目录集中记录本次讨论的问题，逐项补充证据、处理进度和验收结果。它是问题台账，不替代[项目任务主表](../开发路线与任务清单.md)。记录问题不代表已经修复；源码核查也不代表 Windows 实机复现。后续新问题每次单独追加一项。

## 已确认正常的范围

用户明确确认：通过“策略与预设”推送网站黑白名单正常，学生端实际生效，到期自动解除正常。Veyon 本身也正常。这些能力不能笼统归入“策略未验收”。该结论限于用户实际使用的环境和流程，不扩大到课堂策略、AppLocker 或全部 Windows 版本。

## 问题索引

| 编号 | 问题                                             | 依据及状态                                       |
| ---- | ------------------------------------------------ | ------------------------------------------------ |
| 001  | 稳定版更新可能使用错误的 Agent 暂存目标版本      | 修复源码和定向夹具已通过；线上 `.56` stable Schema 3 更新及 Windows VM 仍未测 |
| 002  | 配置同步后请求处理仍可能使用旧配置               | 本次 review；源码逻辑仍存在，待修复验证          |
| 003  | 核心课堂功能及部分系统策略缺少通过现场验收的证据 | 用户现场反馈；网站黑白名单流程除外               |
| 004  | 单一“最新版本”提示不足以表达组件和恢复状态       | 用户现场反馈；界面现状需进一步核查               |
| 005  | 更新失败后回滚也可能失败，需要人工恢复           | 历史现场故障仍待复测；P0-01 已加配置同步门禁（FIXED-CODE） |
| 006  | 课堂通信故障的诊断反馈不足                       | 用户现场排障过程；当前界面覆盖范围待核查         |
| 007  | 更新切换前的回执被按目标 Agent 版本校验          | 已有 review 提出，尚未在逐项对话展开；待修复验证 |
| 008  | 未知目标 Agent 版本被当作无需更新                | 已有 review 提出，尚未在逐项对话展开；待修复验证 |
| 009  | AppLocker 应用限制未覆盖商店和打包版游戏         | 本次单独补充；源码确认，Windows 效果待验收       |
| 010  | 现场运行的 Agent 未由预期 SYSTEM 启动任务托管    | 用户 PowerShell 显示任务缺失但进程运行；待恢复及重启验收 |
| 011  | 正式 StudentSetup 安装包没有内嵌 Authenticode 签名 | 三份已发布安装包的 PE 证书目录均为空；来源及摘要已实测，影响与发布策略待处理 |
| 012  | CloudBase 迁移顺序冲突缺少安全的选择性应用路径   | allowlist helper 与离线夹具已写；CLI 和隔离环境验收未执行             |

## 001：稳定版更新可能使用错误的 Agent 暂存目标版本

- **影响：** 稳定版安装包携带较新的 Agent，但清单未声明 `StudentAgentVersion` 时，暂存可能指向旧版 Agent 目录，文件冲突导致更新失败或回滚。
- **依据：** 原代码读取内置 `bundledAgentVersion` 后重设事务目标，但 `StageAgentUpdate` 和切换仍使用此前计算的 `targetAgentVersion`。这会影响没有 `StudentAgentVersion` 声明的稳定 Schema 3 清单。
- **处理方向：** 验证安装包内 Agent 版本后，暂存、激活、交接使用同一目标版本。
- **验收要求：** 使用未声明 Agent 版本的稳定清单，从旧 Agent 更新到较新内置 Agent；确认事务提交和实际运行版本。
- **线上范围（2026-11-03）：** 用户确认当前线上主流 Agent 为 `0.4.56`；本轮只优先验证 `0.4.56 → StudentSetup 内置 Agent 0.4.57`，`0.4.55` 兼容矩阵暂缓，除非装机盘点发现仍在使用。
- **工作区修复：** `StudentApplicationUpdateProcessor.ResolveTargetAgentVersionAfterSetupInstall` 在验明包内 Agent 版本后，以该版本作为唯一暂存/交接目标，并同步 rebasing 事务目标；显式签名目标不一致或包内 Agent 降级仍会拒绝。当前补丁进入工作区构建的 Agent `0.4.57`；它不会改写线上已安装的 `0.4.56` 二进制。
- **自动化证据：** `dotnet run --project tests/VeyonCampus.Checks -c Release --no-restore -p:NuGetAudit=false -p:UsedAvaloniaProducts= -- --student-application-update-fixtures` 通过，覆盖清单省略 Agent 目标时 `0.4.56 → 0.4.57`、显式目标冲突和降级拒绝。构建有既有 `NU1900`/`CA1416` 警告；`git diff --check` 通过。
- **状态与缺口：** 改动尚未提交。Windows VM 上由线上 `0.4.56` 执行稳定 Schema 3 下载、暂存、切换、健康读回仍 `NOT-TESTED`，需用正式发行流水线生成的稳定签名包验证。截图中的 local-test 拒绝发生在教师端预检，是独立的信任/能力门禁，不等同于 stable 升级失败；测试 bootstrap 安装 Agent `0.4.57` 也不能代替 `.56` 首次升级验证。

## 002：配置同步后请求处理仍可能使用旧配置

- **影响：** 教师地址、证书已写入磁盘并通知连接管理模块重载，但请求处理继续报告旧配置指纹；后续更新健康校验可能与重启后的配置不一致。
- **依据：** [WebsitePolicyRuntime.cs](../../src/VeyonCampus.Core/WebsitePolicyRuntime.cs) 的请求分发传入启动时读取的 `config`；`HandleConfigurationSyncAsync` 写文件并请求 supervisor 重载，未更新请求处理使用的配置。
- **处理方向：** 同步成功后统一更新活动配置，使请求处理、状态回执、更新事务使用一致的配置快照。
- **验收要求：** 同步教师地址及证书后，无需手工重启即可读回新配置指纹；随后执行更新健康检查。
- **边界：** 不能直接认定它就是现场长连接失败的唯一原因；现场还发现教师地址为空、根证书未配置。

## 003：核心课堂功能及部分系统策略缺少通过现场验收的证据

- **现象：** 用户反馈教师与学生无法双向通信、学生端等待教师连接、课堂策略无法正常应用；用户明确表示 AppLocker 和系统策略没有验收成功。
- **影响：** 安装成功、构建通过或收到策略回执，不能证明学生机真实执行了课堂限制，也不能证明结束课堂及重启后的恢复行为正确。
- **处理方向：** 按功能、Windows 版本和账户权限建立现场验收记录，标明已通过、失败、未验证，避免用一个总状态概括所有能力。
- **验收要求：** 在真实双机环境检查连接、消息往返、课堂策略实际效果、结束及异常恢复。AppLocker 和系统策略分别验收。
- **纠正之前的表述：** 网站黑白名单推送、生效、到期解除已获用户确认，明确排除在本项未验收范围之外。

## 004：单一“最新版本”提示不足以表达组件和恢复状态

- **依据：** 用户同一次故障调查中提供安装版本 `1.1.44`、运行 Agent `0.4.55`、事务状态 `recoveryRequired`，并反馈界面显示版本已最新但通信异常。
- **影响：** 教师难以判断安装是否完整、运行组件是否匹配、是否需要修复。不同组件的版本号不同本身不是故障，必须按发行包的对应关系判断。
- **处理方向：** 分别呈现安装版本、运行 Agent 版本、更新恢复状态及课堂连接状态。
- **验收要求：** 对完整安装、运行旧 Agent、恢复未完成及课堂断连分别检查显示是否准确。
- **状态：** 已确认现场出现混合状态；当前各页面是否已充分表达这些信息仍需检查，不能声称所有界面都没有相关字段。

## 005：更新失败后回滚也可能失败，需要人工恢复

- **依据：** 用户日志显示移动 `Student` 目录时 `Access denied`，回滚又无法找到目标 Agent 文件，事务进入 `recoveryRequired`。随后诊断显示旧 Agent 文件存在、目标 Agent 文件不存在、旧 Setup 备份不存在。
- **影响：** 旧 Agent 仍可运行网站策略，但完整安装是否恢复无法据此确认。若批量触发，将增加逐台维修工作。
- **处理方向：** 核查目录移动失败条件及回滚对实际备份/目标文件的依赖，完善失败分支和可操作的恢复入口。
- **验收要求：** 对安装前、备份、安装、Agent 切换等关键阶段注入失败，验证旧程序、启动配置及健康读回均恢复，或明确报告需要人工处理的步骤。
- **边界：** ACL 输出显示管理员和 SYSTEM 有权限，不能仅凭异常就认定是普通目录权限配置错误；实际根因仍需定位。该现场故障是否已恢复尚未确认。
- **相关防护 P0-01：** 提交 `13124e9` 让统一维护取得每台学生机的结构化更新结果。`PendingVerification`、事务进行中/需恢复、回滚、失败、身份变化、缺失或未知状态都不会进入配置同步；只有签名读回确认无需更新，或更新已提交且健康/版本校验通过的设备才可继续。单台阻塞不会影响批次中已确认安全的其他设备。
- **P0-01 自动证据：** 2026-11-02，基线 `930af1a`；检查命令 `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks -c Release --no-restore -- --student-application-update-fixtures` 退出码 0；TeacherConsole Release 构建 0 warning / 0 error。`git diff --check` 通过。
- **P0-01 状态：** `FIXED-CODE`；Windows VM 的旧 Agent `PendingVerification` 与配置同步竞态为 `NOT-TESTED`。这项防护避免未闭环时继续操作，但没有修复旧现场的目录访问拒绝或回滚缺失文件问题；Issue 005 整体仍未关闭。
- **变更位置：** [TeacherViewModel.cs](../../src/VeyonCampus.App/TeacherViewModel.cs)、[StudentMaintenancePlanning.cs](../../src/VeyonCampus.Core/StudentMaintenancePlanning.cs)、[StudentApplicationUpdateChecks.cs](../../tests/VeyonCampus.Checks/StudentApplicationUpdateChecks.cs)。实施步骤与逐项证据见[最终实施计划](../VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md)。

## 006：课堂通信故障的诊断反馈不足

- **依据：** 用户看到“等待教师端连接”，通过多轮 PowerShell 排查才查出 `TeacherChannelEndpoints` 为空、根证书未配置。网站策略和 Veyon 正常并不能证明独立课堂通道正常。
- **影响：** 教师难以区分未配置、证书失败、网络不可达及服务未监听；机房批量排障成本高。
- **处理方向：** 自动检查必要配置，集中展示逐台连接阶段、失败原因及最后成功连接时间，提供有针对性的恢复说明。
- **验收要求：** 分别模拟地址缺失、证书错误、端口不可达和正常连接；教师端应明确区分且不误报策略全失效。
- **状态：** 现场反馈不足已确认；代码中已有日志或状态字段是否可复用，需要继续核查。

## 007：更新切换前的回执被按目标 Agent 版本校验

- **来源：** 本次 review 已有 P2 问题，不是新增发现。
- **影响：** local-test 更新返回 accepted 时旧 Agent 仍在运行，回执带旧版本；教师要求目标版本会提前拒绝有效回执，可能把实际成功的更新显示为需核对。
- **依据：** [StudentApplicationUpdates.cs](../../src/VeyonCampus.Core/StudentApplicationUpdates.cs) 调用响应校验时传入 `source.TargetAgentVersion`，待重启结果则由当前 Agent 版本生成。
- **处理方向：** 区分切换前回执与终态读回，分别使用当前版本和目标版本校验；同时核查后续终态与初始回执的版本比较。
- **验收要求：** 覆盖 Agent 版本改变的 pending→committed 过程，既接受合法切换，也拒绝错误目标或身份。

## 008：未知目标 Agent 版本被当作无需更新

- **来源：** 本次 review 已有 P2 问题，不是新增发现。
- **依据：** [StudentMaintenancePlanning.cs](../../src/VeyonCampus.Core/StudentMaintenancePlanning.cs) 在 `TargetAgentVersion` 为 null 时令 `agentComparison = 0`。
- **影响：** 稳定清单未声明 Agent 版本、Setup 已到目标版本但运行 Agent 较旧时，维护计划可能跳过能修复 Agent 的安装流程，并报告软件达标。
- **处理方向：** 单独表达目标未知，不能把未知等同版本已达标；定义可靠的内置版本核验或修复安装路径。
- **验收要求：** 覆盖 Setup 同版本、Agent 旧版本、稳定清单无 Agent 版本的维护场景，并确保不会降级更高版本。

## 009：AppLocker 应用限制未覆盖商店和打包版游戏

- **追加日期：** 2026-11-02。本轮只追加这一项新问题。
- **依据：** [ApplicationPolicy.cs](../../src/VeyonCampus.Core/ApplicationPolicy.cs) 的 Appx 集合创建“已签名打包应用放行基线”，对所有人使用 Allow，发布者、产品和二进制名称均为 `*`，版本范围不限；注释明确该实现仅面向桌面 EXE。
- **影响：** 现有应用规则无法针对 Microsoft Store、Microsoft Solitaire Collection 等打包应用实施限制。用户已报告上课玩纸牌及从商店下载游戏的需求，当前 EXE 管控范围不足以满足这一需求。
- **已有能力边界：** [StudentSystemPolicy.cs](../../src/VeyonCampus.Core/StudentSystemPolicy.cs) 存在 `RemoveWindowsStore` 策略写入，因此不能说项目完全没有商店限制代码；但它的真实效果尚未通过用户验收，也不能据此证明已安装游戏会被禁止运行。
- **处理方向：** 增加针对实际包身份的打包应用规则，限定学生账户并保留正常系统应用；将禁止打开商店和禁止运行已安装游戏分别设计、显示和验收。
- **验收要求：** 在实际 Windows 10/11 版本及学生账户上，分别验证打开商店、下载游戏、运行已安装纸牌、撤销恢复；记录系统版本、应用包身份及 AppLocker 执行日志。此处不承诺所有 Windows 版本均已支持或已通过。
- **状态：** 功能覆盖缺口已由源码确认。本次只记录，未修改策略实现或运行 Windows 测试。

## 010：现场运行的 Agent 未由预期 SYSTEM 启动任务托管

- **现场证据：** 用户提供的管理员 PowerShell 输出为 `NO_MATCHING_TASKS`；同一轮进程清单显示 `VeyonCampus.Agent.exe` 正在运行，路径为 `C:\ProgramData\VeyonCampus\WebsitePolicyAgent\0.4.55\VeyonCampus.Agent.exe`。之后 Agent loopback 健康检查返回 `200 ready`。这证明当时进程可响应，不证明重启后的启动链或更新恢复链正常。
- **源码依据：** [WebsitePolicyRuntime.cs](../../src/VeyonCampus.Core/WebsitePolicyRuntime.cs) 将 `VeyonCampus-WebsitePolicyAgent` 作为 SYSTEM 开机启动任务；Agent 健康快照在任务缺失时把 `taskMatchesAgent` 和 `taskAclRestricted` 标为 `false`。更新恢复任务的注册也要求主 SYSTEM Agent 任务已存在且动作、配置、ACL 均读回匹配。
- **影响：** 现场进程的启动归属和持久性未得到确认。若缺少其他有效启动入口，注销或重启后 Agent 可能无法自动启动；恢复任务也不能通过预期主任务链注册。`200 ready` 单独不能代表已安装任务及恢复链完整。
- **边界：** 当前证据没有证明 Agent 在重启后必然无法启动，也没有确认是否存在其他受支持的启动入口；应按“主任务缺失、启动/恢复完整性待验证”处理，不据此断言具体根因。
- **处理方向：** 在 Windows 实机只读取证 Agent 主任务、恢复任务及 watchdog 的存在性、SYSTEM 身份、动作路径、配置路径和 ACL；查明当前进程由何处启动。若由安装修复恢复任务，需验证读回一致，并在维护窗口重启后检查 Agent 与恢复任务。
- **验收要求：** 记录重启前后 PID、实际运行版本、签名健康状态、主任务及恢复任务读回；确认主任务缺失能在教师端被识别为安装不完整，而非仅显示 Agent 在线。
- **状态：** 用户现场报告确认主任务查询无匹配项且 Agent 进程运行；来源未核实、重启行为未测试、修复未验收。本条记录问题，不执行远程修复或学生机变更。

## 011：正式 StudentSetup 安装包没有内嵌 Authenticode 签名

- **证据：** 实际下载的 `v1.1.40`、`v1.1.41`、`v1.1.44` GitHub Release 均标记为非预发布；对应 StudentSetup EXE 的 PE 头都显示 `CertificateTableRVA=0`、`CertificateTableSize=0`。三份文件的本地 SHA-256 均与 GitHub Release API 公布值一致；production `/v3` 返回的 1.1.44 安装包也与线上 manifest 一致。对应 GitHub Release 资产仅有 `SHA256SUMS` 和安装 EXE，没有 `.cat` 目录签名文件。摘要、`ProductVersion` 与 PE 证书目录读回见[历史兼容性矩阵](../maintenance/legacy-compatibility-matrix.md)。
- **影响：** Windows 无法从安装器自身验证发布者证书；运行时可能显示“未知发布者”，SmartScreen 的发布者信誉也无法由该 EXE 的 Authenticode 签名提供。自动更新使用的 RSA-PSS 清单验签和 SHA-256 校验属于另一条信任链，不能给用户首次下载后手动启动的 EXE 添加 Windows 发布者身份。
- **边界：** 当前 Beta 工作流文本明确提示安装器未 Authenticode 签名，但上述三个 GitHub Release 均非预发布，且 1.1.44 已出现在 production 发行目录。应明确稳定版的签名和下载发布政策；本条不声称 RSA 更新验签失效。
- **处理方向：** 若这些安装包用于正式分发，在最终打包后由受控 Windows CI 使用组织证书签名；同时在下载页明确当前签名状态，避免把 Beta 的限制带入正式渠道。
- **验收要求：** 从待发布的 GitHub 与 production 下载实际安装器，在 Windows 10/11 上读回 `Get-AuthenticodeSignature`，验证签名状态、发布者和信任链；改字节后签名必须失效。仅做 SHA-256 或 RSA 更新清单验签不算 Authenticode 验收。
- **状态：** 三份已发布 StudentSetup 文件缺少内嵌 Authenticode 已由 PE 证书目录确认。是否作为新稳定发布门禁，以及现有线上下载页需要怎样标注，尚待处理；本次没有重新签名或替换发行包。

## 012：CloudBase 迁移顺序冲突缺少安全的选择性应用路径

- **证据：** 2026-11-03 对唯一配置的 production 环境执行只读 `tcb db pg migration up --dry-run --json`，结果为 `dryRun=true`、`executable=false`；待应用的目标迁移为 `20261021140000_create_file_update_release_catalog` 与 `20261102120000_support_signed_student_agent_targets`，同时报告本地 `20261010090000_add_daily_operations_reporting` 早于远端 ledger 最新版本。当前 CloudBase CLI 3.8.5 的本地帮助显示 `--include-all` 会允许 out-of-order migrations。dry-run 没有执行写入。
- **源码依据：** [deploy-cloudbase-api.sh](../../scripts/deploy-cloudbase-api.sh) 说明 OperationsReporting 仍禁用，故意不把旧 `20261010090000` 列入部署必需迁移；[cloudbaserc.json](../../cloudbaserc.json) 将 `OperationsReporting__Enabled` 设为 `false`。该部署脚本只检查目标 migration 是否已应用，不应用 migration。目标 API 部署需要两条新迁移已在目标环境的 ledger 中。
- **影响：** 当前没有已验证的流程能在隔离环境应用两条目标迁移，同时保证旧的禁用迁移不被意外执行。直接照默认 `migration up` 预演无法继续；盲用 `--include-all` 可能应用被部署脚本明确排除的 OperationsReporting migration，造成额外 schema/函数变更。
- **边界：** 本次未尝试 `--include-all`、没有应用任何 migration，也没有改动 production。尚未证明该 CLI 选项实际会执行哪些文件；具体应用集合必须在安全、隔离的环境和明确迁移策略下确认。
- **处理方向：** 明确可审计的迁移选择机制和版本 ledger 处理方式，使指定迁移按依赖顺序应用、其他禁用迁移保持未应用；在隔离 CloudBase 环境验证成功、失败回滚及重跑幂等性，再用于后续部署。
- **代码进展：** 新增 [apply-cloudbase-release-migrations.sh](../../scripts/apply-cloudbase-release-migrations.sh) 和离线夹具 [apply-cloudbase-release-migrations.test.cjs](../../scripts/apply-cloudbase-release-migrations.test.cjs)。脚本构造只含两条目标 SQL 的临时 migration 目录，通过 CLI 支持的 `database.migrations` 路径配置执行预演；先核对远端必需基线并拒绝已含禁用 OperationsReporting migration 的环境；应用时必须提供目标环境 ID 的二次确认及精确 migration ID 清单，成功后再读回 ledger，并拒绝仓库配置的 production envId。离线夹具已加入 API contract CI，但本轮按用户要求未运行；CLI 在真实隔离环境中的迁移选择和执行结果仍待验收。
- **验收要求：** 从与 production 相同迁移基线的隔离环境演练。验证只应用预期 migration ID、ledger 与 schema 一致、OperationsReporting 仍未部署/启用；故障中断后可安全续跑。部署脚本的 migration preflight 通过后再验收 v4 API 与发行签名。
- **状态：** 选择性迁移代码为 `FIXED-CODE`，尚未通过 CLI/隔离环境验收；P0-02 的 CloudBase 部署前置仍未关闭，未对 production 写入。
