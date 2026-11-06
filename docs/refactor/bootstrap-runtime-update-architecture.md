# P0：Bootstrap、动态配置与程序更新三层解耦

状态：源码审查完成第一轮；尚未确定最终协议，也未改生产配置/云端。本文把用户提出的架构目标加入重构任务，作为设计基线，后续按测试和兼容矩阵迭代。

## 目标

学生机只需一次受信任 Bootstrap。此后：

1. 校区身份、设备 Agent 身份和初始信任锚由 Bootstrap 建立。
2. 网站/应用/系统限制、教师连接、以后新增的运行配置模块独立签名、版本化和对账；新增模块不要求修改学生 Setup 包 schema。
3. Agent、StudentSetup、Maintenance 程序发行按各自产品版本和事务更新；程序更新不隐式改变运行配置，运行配置也不需要重装程序。

Schema 3–8 是引导输入格式的历史版本；不把 schema 8 变成新的永久总包，也不要求新增模块按 9、10、11……持续扩展 Bootstrap 包格式。

## 当前已确认的实现

| 能力 | 当前实现 | 缺口 |
|---|---|---|
| 旧包读取 | `PackageManifest.Load` 接受 Schema 3–8；Schema 3 有校区 Veyon 公钥、网站策略公钥和遥测字段 | Schema 3 不包含应用/系统策略公钥或教师连接端点/根证书，Agent 首装可缺少后续功能需要的信任/连接配置 |
| 配置包演进 | Schema 4 加应用策略 key，5 加系统策略 key，7 加 teacherChannel，8 加双签名 PolicyTrustRotation；`CampusPackageSchemaMigration` 对 schema 变化要求显式重建 | 一次性部署包逐步累积运行时功能和信任材料，导致旧校区为了新模块被迫重建包/手工部署 |
| Agent 配置 | `WebsitePolicyAgentConfig` 是跨功能字段集合；`ConfigFingerprint` 汇总所有信任根、端点、版本等为单个整机指纹 | 无独立模块版本/Revision/状态；一个模块改动和其他配置耦合 |
| Teacher 连接同步 | `StudentAgentConfigurationSyncCommand` 是 schema 1，只支持教师端点和 TLS 根；Agent 校验当前 Agent 指纹与校区/信任快照，原子写回、健康读回后回签 | 同步命令不能承载通用模块；Agent 写配置后重启健康失败的恢复合同与 SYSTEM Maintenance 的恢复路径不一致 |
| Maintenance | Protocol v1 有签名请求/响应、设备身份绑定、nonce、防重放、`getCapabilities`、`syncTeacherEndpoint`、软件事务；独立 SYSTEM service 保存端点 Revision 和故障恢复文件 | `getCapabilities` 未报告运行配置模块/模块 schema；`syncTeacherEndpoint` 仍是专用字段 |
| Desired / Reported | `MaintenanceJournal` 已保存按设备身份+模块区分的 `DesiredMaintenanceConfiguration`、`ReportedMaintenanceConfiguration` 和 `PendingMaintenanceConfiguration`，有 revision 单调与 fingerprint 对账 | 实际队列/协调器只为 `TeacherConnectivity` 工作；Pending 类型本身只有端点和 TLS 根字段；`SchemaVersion` 固定传 1，没有通用签名载荷或模块 validator |
| 离线重试 | Teacher 工作流持久化教师连接 Desired；Agent 连回 Teacher 后触发 `ReconcilePendingMaintenanceConfigurationAsync` | 重试入口依赖 Teacher UI/旧状态连接回调；尚无对任意配置模块工作的独立队列和 Reported 回执 |
| 模块密钥 | 网站、应用、系统策略有不同签名器与策略文件；Schema 8 的旋转授权要求旧根授权、校区/用途匹配、候选根和包内 key 一致 | 老 Schema 3 设备没有后加的应用/系统根；如何用既有信任锚安全委派新模块根尚未形成通用合同 |
| 程序发行 | StudentSetup/Agent/Maintenance 各有签名更新和事务恢复 | 能力与配置报告仍混在当前 Agent/安装包/Release policy capability 中；需要明确矩阵而不是假定新版程序自动意味着新版配置 |

## 共用根因

运行配置的**数据形状、信任根、传输命令和版本声明**目前分别嵌在 Campus Package Schema、`WebsitePolicyAgentConfig`、Agent 专用端点同步命令、Maintenance 专用 endpoint command 和程序 Release Manifest 内。Maintenance 已有通用的 Desired/Reported 数据模型，但下游 Payload/Agent 应用仍是专用的 `TeacherConnectivity`，所以只有 journal 层“像通用配置管理”，没有端到端通用模块协议。

结果是新增功能常要增包字段、增 Agent 构造字段、增包 schema 和增各自同步路径；schema 迁移与正常配置迭代被混为一件事。只把 Schema 8 改成 Schema 9 不会解除该耦合。

## 目标边界与协议草案

### A. 一次性 Bootstrap

- 只含固定校区 ID、Student Agent 身份绑定/公钥指纹、最初可用的教师连接引导，以及已确认可作“配置委派根”的公钥和用途。
- 保持受信任根不可静默变更；Bootstrap 由现有 Package Schema 3–8 一次性解析为当前 Agent 本地信任状态。
- 对已安装的 Schema 3 设备，优先使用其既有 WebsitePolicy 根作**目的限定、协议域分离的配置委派签名根候选方案**，让其签署模块注册表/信任委派；应用/系统模块 payload 仍由各自模块专用根签名。此候选需经过安全审查和角色/密钥存储审查；不能因为“新公钥签了自己”就接受，也不能把尚未固定的云端公钥直接 pin 入设备。
- 若旧 Agent 没有可验证的配置协商/通用应用能力，先经已签名 Agent 程序更新获得该能力；若其自身没有受信任的远程 Agent 更新能力，报告 `needsManualBootstrapRepair`，不得假称无需部署。

### B. 动态配置模块

通用 Envelope 至少绑定：`CampusId`、固定 Agent identity fingerprint、`ModuleId`、模块 schema、模块 capability/version、单调 Revision、payload fingerprint、签名 key role/fingerprint、issued/expiry、operation/command ID、目标当前配置 fingerprint、payload。拒绝未知必需字段/模块、跨校区、跨设备、回放、降级 revision、冲突 key role 及无兼容声明的 schema。

Agent/Maintenance 先通过已认证并验签的能力报告协商模块 ID、支持 schema 区间、应用器版本、可否回滚。Teacher 只发送双方兼容的 Desired；设备签名 Reported 含模块、schema、revision、fingerprint、`Pending/Applied/Rejected` 及采集时间。Journal 只在签名设备读回与 Desired 一致时置 `InSync`。

模块应用走单一受保护适配器：解析/验证 → staging → 保存上一份原始字节和受管副作用快照 → 幂等应用 → 读回/健康确认 → 原子提交；失败恢复旧配置及模块副作用并读回。不可完整回滚的系统设置模块必须声明补偿操作和 `NeedsReview` 终态，不能谎报原子成功。

### C. 程序更新

继续复用现有签名发行包与 StudentUpdateAtomicTransaction / Maintenance update executor；程序包只声明 Agent / Setup / Maintenance 版本、最低/最高协议和能力，不携带日常校区运行配置。更新 Agent 后重新协商能力、再独立收敛 Desired 配置。

## 旧设备与信任迁移规则

- Schema 3–8 都保留只读解析和原字段语义；schema 3 不假装含有缺失的应用/系统/教师通道配置。
- Capability 未知或签名报告无效：按旧路径/已支持旧协议只执行明确可证实的操作；新模块标记 `Unsupported/NeedsReview`，不向旧 Agent 发送扩展命令。
- 模块密钥轮换需要当前已信任的授权根签名的候选 key 委派；如果配置涉及更改该授权根自身，还要旧根签名 + 新根证明 + 角色和生效时间校验；轮换期间保留旧根直到设备 Reported 确认并满足退休策略。
- 教师地址变更是 `TeacherConnectivity` 模块，不改校区身份、模块根或其他 payload。端点 TLS 根变更须通过当前可信签名链；回滚材料覆盖 Agent 与 Maintenance 两端快照。
- 失败或离线不覆盖已生效配置；Pending Desired 保持 durable、按 identity+module+revision 幂等重发，收到更高 revision 后不重放旧 envelope。

## 核心回归矩阵

| 场景 | 合成输入 | 必须观察到 |
|---|---|---|
| Schema 3 旧校区升级 | 只带校区 key + WebsitePolicy key 的旧包与可兼容旧 Agent | 包原始字段不丢；协商显示旧能力；已信任授权路径可加配置协议/Agent 能力；未配发的模块不误报已启用；全程不重新部署 Setup |
| 新增动态模块 | Agent 已支持、新 ModuleId、新 module schema 和模块专用密钥 | Bootstrap schema 不变；协商后签名下发并读回 InSync；旧设备明确 Unsupported；坏签名/未知必需 capability 拒绝 |
| 教师地址更换 | 当前端点 Desired revision N → N+1，替换 IP/hostname 与 TLS root 组合 | 固定 Agent 身份、校区和模块 key 不变；两个端点侧写入能协调；写后重启/回执失败则恢复原始端点与证书，报告非 InSync |
| 设备离线后重连 | Desired N+1 入队，期间离线；后续已验签 Agent 通道重连 | 只送该 identity/module 最新 revision；重复通知幂等；设备签名 Reported Applied 后队列清除，未读回前保持 Queued/AwaitingReport |
| 密钥轮换 | 旧根签名的候选模块根/策略根轮换声明，之后分别 Reported | 旧根与新根重叠有效；新 key 必须和已签名候选 fingerprint 匹配；未确认设备不退休旧根；自签未知 key、篡改 key、跨角色 key 均拒绝 |

每个场景都补失败变体：重复/倒退 revision、断电点/原子替换中止、Health 旧 fingerprint、响应旧 nonce、切换校区、Agent 更新期间并发配置写、回滚文件缺失/链接路径；按边界只做本地合成夹具，不联真实学生机或生产环境。

## 实施顺序

1. 继续只读盘点 Agent/Maintenance 信任存储、能力响应、事务锁、端点双写与更新交接，并完成 threat model；把“Schema 3 是否能安全作为配置委派根”作为首要决策，而不是预设已解决。
2. 建立协议模型/compatibility matrix 和五个核心回归测试；先测旧 Agent 能力协商与 schema 3 配置读取。
3. 将现有 `TeacherConnectivity` 迁为通用模块 envelope 的第一个模块，复用 Maintenance journal、签名协议、更新 machine lock、原子存储与 recovery 文件；删除/停用重复的 Agent-only writer 前确保旧协议适配器仍可用。
4. 接入 capability negotiation、Generic Pending Desired / signed Reported、离线重试和读回；旧协议只用于旧能力设备。
5. 把新增策略/模块 trust configuration 迁到通用模块层；复用 PolicyTrustRotation 的授权语义，确保系统/应用 side-effect adapters 原子补偿。
6. 运行对应 Core/Agent/Maintenance fixture 和发布 artifact compatibility tests；Windows 隔离 VM 验证由用户最后验收；未通过前不发包、不改生产。
