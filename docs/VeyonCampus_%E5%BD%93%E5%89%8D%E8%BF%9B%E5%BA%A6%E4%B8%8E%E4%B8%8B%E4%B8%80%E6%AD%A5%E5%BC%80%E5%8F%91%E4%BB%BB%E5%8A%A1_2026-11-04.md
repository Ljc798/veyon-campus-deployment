# Veyon Campus：当前进度评估与下一步开发任务

> **审查日期**：2026-11-04（北京时间）  
> **仓库**：[Ljc798/veyon-campus-deployment](https://github.com/Ljc798/veyon-campus-deployment/tree/develop)  
> **远端基线**：`develop` — [`9b07fd220ed2c130ede2f15057c87f9a8ea60db8`](https://github.com/Ljc798/veyon-campus-deployment/commit/9b07fd220ed2c130ede2f15057c87f9a8ea60db8)  
> **对比基线**：`aee067609afc390c347ac93db3f8b8ecf062670b`，之后新增 **8 个提交**。  
> **适用范围**：Windows 教师端、StudentSetup / Agent、独立 MaintenanceService、Veyon 目录协同、移动端 PWA、发布工程。  
> **评估性质**：基于远端代码及仓库内实施/现场记录；不等于当前 HEAD 的完整自动化测试或实机验收报告。

## 一、总体结论

**Maintenance 核心架构和主要执行路径已基本成形，但项目尚未达到可正式批量部署的发布标准。** 当前的主要障碍已从“缺少核心功能”转为“设备身份与目录一致性、跨模块配置闭环、恢复可靠性、发布工程”四方面。

- **Maintenance**：Coordinator、独立服务通信、签名身份、持久事务、受控更新、状态读回、故障恢复机制、熔断与人工介入入口均已有代码。仍存在跨模块配置同步、密钥生命周期的统一约束、正式签名与发行闭环等缺口。
- **地点与座位管理**：最近集中修复了重复地点、顶层 Location 被误丢弃、错误删除档案、右键删除及 UUID 关联问题。此处仍是当前最敏感的工程风险区。
- **发布**：已有 TeacherConsole `1.1.67` 测试安装包的成功构建记录；最新提交还修改了顶层地点解析及其测试夹具。仓库记录提及用 `1.1.68` 验证，但**尚未证明 1.1.68 安装包已完成构建或验收**。
- **验收分级**：`FIXED-CODE` ≠ `BUILD-PASS` ≠ `UNIT-PASS` ≠ `VM-PASS` ≠ `FIELD-PASS`。不要把提交、代码审查或本地构建误报为正式可用。仓库主计划的 **M0–M4 里程碑尚未通过阶段退出门槛**。

### 当前模块状态

| 模块                                         | 代码状态判断                   | 主要缺口                                           | 建议优先级   |
| -------------------------------------------- | ------------------------------ | -------------------------------------------------- | ------------ |
| Maintenance Coordinator / Workflow / Journal | 主要执行路径已接入             | 重启恢复、并发一致性、端到端闭环                   | P0/P1        |
| MaintenanceService 独立通道及自更新          | 主要流程已接线                 | 服务迁移、受限恢复、版本切换、旧 Agent 兼容        | P1           |
| 签名身份与密钥轮换                           | 核心机制已实现                 | 各类密钥统一生命周期、审计和旧会话撤销边界         | P1           |
| 设备版本、诊断、人工恢复提示                 | 基础 UI 已接入                 | 实时可信度、状态呈现、信息密度                     | P1           |
| Veyon 目录与座位协同                         | 多轮修复中                     | 单一设备真实来源、统一快照、操作日志、局部失败恢复 | **P0**       |
| 配置 Desired/Reported                        | TeacherConnectivity 路径已接入 | 其他配置模块的完整对账/离线恢复                    | P1           |
| 首次安全配对                                 | 已有身份核对码和人工确认       | 无包首次安全分发/引导流程                          | P2（可延期） |
| 移动端 PWA                                   | 目录健康状态卡已新增           | API 传递真实可信状态、统一设备身份                 | P2           |
| CI / 签名 / CloudBase / RC                   | 脚本和部分流程已接线           | Checks 构建、正式签名、隔离迁移、同源候选包        | **P0/P1**    |

> **进度数字说明**：仓库设备目录协同实施记录曾估计完整范围代码约 65%、已有地点新增主链路约 75%。这是旧范围的静态开发估算，后续修复又改变了代码；不据此推算整个项目的“完成百分比”。

## 二、最近提交与实际突破

### 2.1 `aee0676` 以来的 8 次提交

| 短 SHA    | 主题                                                       | 与本次评估的关系                                       |
| --------- | ---------------------------------------------------------- | ------------------------------------------------------ |
| `727e2f7` | Expire stale Maintenance identity rotation approvals       | 过期轮换审批失效                                       |
| `3d842d4` | Revoke stale Maintenance sessions after key rotation       | 轮换后旧会话撤销                                       |
| `6a4de11` | Block stale Maintenance sessions during rotation review    | 审批期间阻止不可信会话                                 |
| `846c086` | Revoke Maintenance sessions when removing trusted identity | 删除信任身份时撤销会话                                 |
| `0458734` | Add bounded Maintenance service key rotation trust         | 受限旧密钥回滚信任窗口                                 |
| `1ac7adc` | Retire expired Maintenance rollback trust                  | 过期回滚信任退役                                       |
| `3be1647` | Document Maintenance v1 key lifecycle policy               | 生命周期规则成文                                       |
| `9b07fd2` | important bug fixed                                        | 集中包含地点/座位、PWA、Schema 与 Maintenance 接线修复 |

### 2.2 最新关键修复（按实际问题归类）

1. **重复地点根因**：Veyon 4.11.2 的 `networkobjects import ... location <name>` 在特定情况下会再追加同 UUID 的 Location。`VeyonNetworkObjectDirectory.AddLocation` 改为对已存在地点以父 UUID 逐台 `networkobjects add computer`，只有地点确实不存在时才创建地点。
2. **顶层地点丢失**：`networkobjects dump` 的顶层 Location 可使用全零 Parent UUID。之前解析器将其当成 Root 过滤，导致所有电脑出现“父地点缺失”，影响策略目标、座位绑定及写入。最新解析代码已保留真实 Location，且添加了对应 Veyon 4.11.2 夹具。
3. **删除与身份提示**：增加按电脑 UUID 删除 Veyon 对象的路径、读回核对、用户确认及座位持久化；UI 展示座位档案关联的地点 UUID 与设备数量。
4. **Maintenance 安全**：独立服务身份轮换、过期授权、撤销旧认证会话、受限回滚信任及生命周期文档进一步完善。
5. **配置与交互**：Schema 7→8 显式迁移计划与防隐式推断守卫、PolicyTrust 轮换草稿操作、PWA 目录健康卡等已加入。

**现场事实提醒**：11 月 4 日曾发生误删“真正含 6 条 Veyon UUID 绑定的校区档案”，后根据备份恢复。说明当前不能只按显示名称选择/删除档案，必须建立 UUID 与来源级别的可靠关联。

## 三、P0：除测试以外，必须优先完成的开发任务

### P0-01：统一设备目录及身份模型（UD-06 主线）

**目标**：同一台物理电脑在座位图、Veyon、Agent、Maintenance、监控及手机端中都能被明确、稳定地识别。

**具体工作**

- [ ] 定义统一 `DeviceDirectorySnapshot`（或扩展现有 `VeyonDeviceDirectorySnapshot`），包含 **DeviceId、VeyonComputerUuid、LocationUuid、Hostname/Host、SeatId、ComputerNumber、StudentName、AgentIdentityFingerprint、状态、版本号/生成时间、可信来源**。
- [ ] 明确不变量：`VeyonComputerUuid` 表示 Veyon 对象；`AgentIdentityFingerprint` 表示已固定 Agent 身份；两者不能互相代替。学生姓名与 Veyon Name 仅是显示数据，不能作为主键。
- [ ] 明确 Veyon 目录是**设备/地点事实来源**；本机档案是**座位布局与学生资料来源**。不存在的 Veyon 电脑不能只靠本机模板被自动标记为可下发策略。
- [ ] 对目录完整可信、只读回退、过期快照、UUID 缺失、Host 冲突、Location 迁移分别定义显式状态；不允许“无数据=无电脑”的错误推断。
- [ ] 设备快照按校区/地点和版本发布更改通知，策略、维护、课堂、设备监控共用同一身份与有效性判断，避免每页各自读一套名单。
- [ ] 移动端 `/api/rooms` 后续补充目录可信状态和设备稳定 ID，避免只根据名称/列表数量猜测健康。

**完成定义（DoD）**：在新增、删除、重命名、移动座位及目录读取失败后，各页面能展示相同设备范围；未通过身份核验的设备不能执行高影响操作。

**参考代码**：`src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs`、`ClassroomSeatGridStore.cs`、`TeacherViewModel.cs`、`TeacherViewModel.Classroom.cs`、`MobileWeb/`。

### P0-02：把 Veyon 增删改操作改为可恢复的受控事务

**目标**：不再出现“部分设备已写入，UI 却认为全部失败；重试又制造重复地点/电脑”。

**具体工作**

- [ ] 在写入前固定：当前完整可信快照的指纹、地点 UUID、目标设备 UUID/Host、期望新增/删除差异。
- [ ] 每一步明确 `NotStarted / Applied / Verified / NeedsReview / RecoveryRequired` 等结果，**先持久化操作日志，再执行不可逆写入**。
- [ ] 多台新增中途失败时记录每台结果；再次执行前按 UUID/Host 重新读回，不对不确定结果盲目重发。
- [ ] 删除前提供备份、操作范围预览、按 UUID 定位、二次确认；删除后同时核验目标消失和**其他电脑/地点没有意外变化**。
- [ ] 处理“本机座位已保存，但 Veyon 写入失败”以及反向不一致，给出明确的恢复/重新关联路径。
- [ ] 检查多教师端同时变更同一 Veyon 目录的竞争：本机 named mutex 不等于跨教师机协调；需要确定产品支持边界或加入写入冲突防护。

**完成定义（DoD）**：所有写操作有明确差异和可审计状态；失败不得误报成功；重试不得重复创建或误删对象；未决写入不能被新操作覆盖。

### P0-03：清理地点档案的第二数据源和危险操作语义

- [ ] 将地点档案按 `LocationUuid` 对应（显示名仅供显示），引入“无对应 / 已对应 / 对应冲突 / 只读”的可见状态。
- [ ] 删除 UI 明确区分：**删除本机座位、解除本机电脑绑定、删除 Veyon 电脑、删除本机校区档案**；不同动作不能共用模糊的“删除学生”。
- [ ] 对已绑定设备的校区档案禁止误删或要求单独的完整影响预览、备份及确认。
- [ ] 清理已停用的地点/机房模板写入入口；保留历史本机档案兼容读取，但不能让旧字段重新生成错误设备名单。
- [ ] 历史 Location 解析兼容（顶层全零 Parent）、重复 UUID 去重及 Host 唯一性规则保持集中实现，不让不同读取路径各自作相反判断。

**完成定义（DoD）**：即使多个档案/地点重名，也不能自动合并或误删真实 Veyon 绑定；用户始终能判断哪个是已核对的真实地点。

### P0-04：修复自动化检查项目的编译/入口缺陷

最新现场记录报告 `tests/VeyonCampus.Checks` 存在既有编译问题：`MaintenanceRun.VerifyCapabilities` 签名不匹配，以及 `MaintenanceFaultInjectionChecks.cs` 的顶层局部函数作用域问题。此处属于**要先修改代码才能开始完整测试**的障碍。

- [ ] 修复 API 调用与实际签名一致性、顶层函数可见性与测试夹具入口。
- [ ] 保留本次新增的 Veyon 4.11.2 顶层 Location 解析用例，补充重复地点、单 UUID 删除、部分新增、未知 Host、数据回退用例。
- [ ] 将 Checks 工程编译纳入 CI 阻断条件（随后执行自动化检查时再确认通过）。

**完成定义（DoD）**：所有检查工程可以编译，不因为项目自身破损而无法执行自动化测试。

## 四、P1：正式 RC / Stable 发布前应完成的代码与工程

### P1-01：冻结 Maintenance v1 的架构和事务边界（D-01）

- [ ] 编写一份统一的协议/信任/事务边界文档（避免散落各计划）：Agent、MaintenanceService、TeacherConsole、UpdateHelper 的责任范围。
- [ ] 明确 Legacy `39174` 与独立 Maintenance 通道并存时的唯一更新裁决规则；旧通道退役必须有版本/现场证据，不能按日期硬删。
- [ ] 固定对外协议字段、可选扩展、关键扩展拒绝规则、重放窗口、命令幂等键与版本兼容范围。
- [ ] 统一服务/Agent/PolicyTrust/校区根密钥的轮换窗口、紧急撤销、过期回滚信任及审计记录，不允许新旧会话并行形成“第二条更新链”。
- [ ] 明确真实 `RecoveryRequired` 时人工恢复流程及恢复记录的持久保存周期。

### P1-02：完成配置模块的 Desired/Reported 闭环（CM-01/02/05）

- [ ] 将现在以 `TeacherConnectivity` 为主的 revision/签名对账模型推广到计划中承诺的其他配置模块：CampusIdentity、PolicyTrust、SystemConfiguration、RuntimeSettings。
- [ ] 对每模块定义“期望配置如何产生、谁有权修改、签名 Reported 由谁生成、离线后如何重试、什么状态必须人工介入”。
- [ ] 禁止未完成软件/服务更新期间并发推送配置；读回必须与当前版本和事务阶段一致。
- [ ] UI 显示模块级别的 `InSync / Drifted / Rejected / Pending / ManualRequired` 和具体失败阶段，而不是仅展示总体成功。

**范围控制**：如果首版稳定版只承诺 TeacherConnectivity，则明确冻结其他模块为“后续版本”，不要实现一半却在 UI 暗示完整自动管理。

### P1-03：完成校区 Schema 7→8 的真实迁移与发行兼容

- [ ] 已有 `CampusPackageSchemaMigration.Plan` 允许清单和显式重建守卫；还需把迁移操作接入真正的包升级/回滚执行路径，并保留旧字节快照。
- [ ] 保证 Schema 8 的 PolicyTrust 轮换授权包有可验证的生成、传输、安装、读回与旧根退休链路。
- [ ] `8→9` 目前明确拒绝，因为 Schema 9 未定义；**不要为追求“进度”虚构 Schema 9**。
- [ ] 判断 stable CloudBase 客户端/API 是否支持 Schema 8；未支持前继续阻断正式发行。

### P1-04：发布工程与 RC 同源构建（P5）

- [ ] CI 能统一构建 TeacherConsole、StudentSetup、MaintenanceService，保留构建 SHA 和角色/版本元数据。
- [ ] 修复并运行检查工程后，让编译/测试/安装器构建结果成为发布门禁，而非人工检查日志。
- [ ] 建立同一源码 SHA 的 RC manifest：三个组件版本、安装包 SHA-256、协议版本、签名者、变更记录、目标升级矩阵。
- [ ] 正式 Windows 安装包配置 Authenticode 代码签名证书、时间戳服务、签后验签；缺凭据时保留 `SIGNING-BLOCKED`，不得宣称 stable 已签名。
- [ ] 在**隔离** CloudBase 环境验证仅应用允许清单的 migration、ledger 版本及 API 兼容；没有隔离环境时不能跳过至生产迁移。
- [ ] 版本升级策略对 0.4.55/0.4.56 及更早来源给出清晰兼容/阻断信息，绝不绕过旧版签名信任链。

### P1-05：维护操作台信息架构收敛（P4）

- [ ] 设备概览主要显示：**当前设备身份、Setup/Agent/Maintenance 版本、可信状态、维护事务阶段、失败原因、唯一推荐下一步**。
- [ ] `PendingVerification` 应是“继续核对原事务”，`RecoveryRequired` 应是“暂停新命令、恢复”，`ManualRequired` 应是“现场处理”，`RolledBack` 才可评估重试。
- [ ] 保留高级操作，但不要为同一动作重复添加多个按钮；清理无法反映真实行为的历史操作入口。
- [ ] 设备统计必须注明“本机 Journal 最近记录”，不得伪装为 54 校区全量实时在线统计。
- [ ] 公开/导出匿名诊断文件前执行隐私字段复核，避免主机名、设备指纹、校区 ID 或任意错误文本泄漏。

## 五、P2：首版稳定运行后再实现（建议延后）

- [ ] 完整的无包首次配对与公钥/根证书安全分发（CM-04）。
- [ ] macOS 钥匙串及完整跨平台密钥操作路径（若首版只服务 Windows 校区，可延后）。
- [ ] LDAP / Active Directory 外部目录的完整写入支持；首版建议检测并保持只读。
- [ ] 更复杂的 Appx/MSIX 已安装程序拦截；不要把当前 EXE AppLocker 限制误宣称为全面覆盖。
- [ ] 多教师机、跨校区集中运维与匿名指标汇聚；先确保本机数据语义可靠。
- [ ] 手机端精细运维操作、UI 动画与外观优化；优先只读可信状态及安全命令。

## 六、推荐执行顺序与交付节奏

| 顺序 | 工作包                         | 先决条件                      | 交付结果                           |
| ---: | ------------------------------ | ----------------------------- | ---------------------------------- |
|    1 | P0-04 Checks 工程编译修复      | 无                            | 可执行的检查框架和新增回归用例     |
|    2 | P0-01 统一设备目录/身份        | 明确 Veyon 与本机资料主从关系 | 单一身份快照与跨页面订阅           |
|    3 | P0-02 Veyon 受控写事务         | 统一身份定义                  | 新增/删除/恢复的事务日志和精确结果 |
|    4 | P0-03 消除双数据源危险操作     | 统一身份与事务                | 删除/关联 UI 语义明确、档案安全    |
|    5 | P1-01 Maintenance v1 架构冻结  | 基本身份/恢复机制稳定         | 协议、信任与回滚边界文档           |
|    6 | P1-02 配置同步范围收敛         | 协议冻结                      | 完整可交付范围，未实现模块明确延后 |
|    7 | P1-03 Schema 8 闭环            | 信任模型稳定                  | 明确迁移/回滚和发行兼容规则        |
|    8 | P1-05 运维操作台收敛           | 真实状态模型稳定              | 一个主入口+清晰人工处置动作        |
|    9 | P1-04 RC 与发布工程            | 代码冻结、检查可运行          | 同源候选包、签名/摘要/发布证据     |
|   10 | Windows VM、现场灰度与正式发布 | 上述开发和环境门槛达成        | 可验证的 RC → Ring 0–3 → Stable    |

> 以上顺序是**开发优先级**，不是要求所有测试拖到第 10 步；每个开发包完成后都应具备对应单测/回归用例，最终再进行完整实机矩阵与灰度。

## 七、当前必须保留的安全边界

1. 不得跳过设备固定身份、Developer Release 验签、SHA-256、命令时效、重放保护与事务日志。
2. 不得让 MaintenanceService 提供通用远程 shell、任意 URL 安装或任意路径写入。
3. 不得为了处理重复地点使用 `networkobjects clear` 或 `config clear`；数据清理必须备份、限制差异、读回验证。
4. 不得按 StudentName、Veyon Name 或纯文本地点名称自动替换设备 UUID/Agent 身份。
5. 不得在旧事务结果未确认时重复发送新更新命令；同一设备并发维护必须受控。
6. 不得用本机测试公钥冒充现网旧版本认可的 stable 签名。
7. 生产 CloudBase migration、稳定版发布及真实校区推送必须单独授权。

## 八、Release Candidate 退出门槛（代码 + 证据）

- [ ] 当前 HEAD 全部必须构建的项目无编译错误；Checks 工程可运行。
- [ ] 地点/设备目录同一快照供策略、课堂、维护使用；新增/删除/改名/冲突时状态一致。
- [ ] 所有高风险 Veyon 写入有备份、精确操作日志、读回核验及不确定结果处理。
- [ ] StudentSetup / Agent / MaintenanceService 安装、自更新、回滚与恢复链有可复查的 Windows VM 结果。
- [ ] 旧 Agent 升级兼容矩阵、认证/签名与身份轮换测试完成。
- [ ] 教师端不把签名 ACK、已发送或缓存状态标为系统实际生效。
- [ ] 正式发行签名与 SHA256SUMS 一致；所需发行与 CloudBase 隔离环境准备完毕。
- [ ] 同源 RC 安装包、变更日志、构建 SHA、设备回归及现场灰度证据齐备。

**当前判断**：尚未满足上述全部门槛。不要以“代码大部分都写好了”作为 54 校区批量上线依据。

## 九、给 Codex / WorkBuddy 的下一轮执行说明

> 请以 GitHub `develop` 的最新实际 HEAD 为基线，先读取 `git status`、`git log`、现有进度台账及下列关键文件，**不要覆盖本地未提交工作**。按本文件第六节的顺序推进任务，优先修复 Checks 工程的编译障碍、再完成 UD-06 统一设备身份快照、Veyon 写事务和地点档案安全收敛。每项拆成独立可审查提交，不要一次性重构 TeacherViewModel 全部功能。  
> 每次提交必须记录：修改文件、行为变化、守住的不变量、尚未实现范围、测试夹具与执行结果（若未测试必须明记）、与现场/历史兼容相关的风险、下一项任务。  
> 保留真实 Veyon UUID 与 Agent 公钥身份双重绑定、旧版本签名信任链、Maintenance 事务 Journal、UpdateHelper 和受控回滚。**不允许通过清空目录、忽略冲突、重发未知命令来“修好”数据**。生产发行、云端迁移与校区部署不在本轮自动执行范围内。  
> 当前文档和现场记录已有过时的局部总结；以最新实际代码、现场证据和本轮新增代码为准同步更新 `docs/maintenance/implementation-progress.md` 与原问题台账，明确区分源码已实现与验证通过。

## 十、审查依据（仓库文档）

- [Maintenance 连续实施进度](https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/maintenance/implementation-progress.md)
- [Maintenance 最终实施计划与阶段门槛](https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/VeyonCampus_Maintenance_%E6%9C%80%E7%BB%88%E5%AE%9E%E6%96%BD%E8%AE%A1%E5%88%92_Goals%E4%B8%8E%E9%97%AE%E9%A2%98001-009.md)
- [设备目录双向同步实施记录](https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/maintenance/2026-11-03-device-directory-sync-implementation-log.md)
- [11 月 4 日地点同步现场问题与修复记录](https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/maintenance/2026-11-04-location-sync-field-feedback.md)
- [校区配置包 Schema 迁移说明](https://github.com/Ljc798/veyon-campus-deployment/blob/develop/docs/maintenance/campus-package-schema-migration.md)

---

**一句话执行原则**：先保证设备身份与数据真实、维护事务可恢复，再做完整发布链路；新功能和视觉优化放到可靠版本之后。

## 2026-11-05 RC 前手机流程与维护后台补充

- 手机课堂页新增“一键开启课堂 / 结束课堂”、学生聊天和网站策略推送入口。开课复用教师端当前地点与设备范围，并读回确认；不会连带启用策略。结束课堂沿用教师端对未解除快捷管控、倒计时和未完成课堂任务的保护。
- 课堂范围发生变化时，手机丢弃旧的电脑选择；只有课堂目标与当前 Veyon 目录完整且唯一匹配时才默认选中。开课后读回必须与操作回执的 session ID 和完整电脑范围一致；名单刷新期间暂停策略写入。网站推送入口会预选并折叠已核对的本堂范围，实际推送仍需教师在限制页确认并查看逐台结果。
- 教师端维护待处理设备卡片补充当前 Agent 固定身份匹配状态、签名状态读回时间、Agent 更新事务阶段、短操作/命令标识和状态专属操作建议。维护记录读取失败或未设置校区时清空旧卡片，避免用陈旧项继续操作。
- **当前证据仅为源码修改**；按用户要求，本轮没有运行测试、构建或 Windows/手机实机验收。移动端课堂、聊天、网站推送与维护卡片仍需用户在 Windows 环境验证；全校目录一致性和 RC 退出门槛仍未满足。
