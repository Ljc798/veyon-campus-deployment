# Veyon Campus｜止血式架构重构、回归治理与 UI 精简任务书

> **交给 Codex 的执行指令**：请先读取本文和当前仓库实际代码，按“调查 → 不变量 → 回归测试 → 最小架构改造 → Windows 实机验证 → UI 精简 → 合并验收”的顺序实施。不要把本文当作要求一次性推倒重写。**最终目标不是让截图里的报错消失，而是让地点、设备、课堂、维护这几类问题不再因同一份数据的多个副本和旧状态而反复出现。**
>
> 仓库：`Ljc798/veyon-campus-deployment`。主要技术栈：C# / .NET 10 / Avalonia（AXAML）/ Windows Service / Inno Setup / Veyon。核查基线：2026-11-05 的远端 `develop`，当时 HEAD 为 `1a4cfb55afde`；执行前**必须再次 fetch 并以实时 HEAD 为准**。

## 0. 先处理另一个分支的合并问题（必须第一步执行）

**用户希望把 `experiment/skills-evaluation` 做出的有效代码整合进 `develop`，但绝不能重复合并，也不允许因此覆盖正常运行的代码或本地未提交改动。**

### 已知 GitHub 事实（核查时点）

- 远端分支列表仅有 `develop`、`main`、`release/basic-v1`，没有名为 `experiment/skills-evaluation` 的远端分支；实验分支可能只存在于本地 Worktree。
- 实验提交 `e1f3d78`（地点 UUID）、`149d319`（UI 精简）、`7cc871b`（实验报告）、`d259d49`（合并建议说明）均已是远端 `develop` 的祖先；**这些代码已经进入 `develop`，不要重做 merge，也不要重复 cherry-pick**。
- 实验报告中的“尚未合并”是该报告生成时的状态，不能当作目前事实。
- 最新 `develop` 已包含安装器 `d4ccb3b` 的维护服务状态复核；后续 HEAD 可能再次变化。

### Codex 先运行并报告

```bash
git status --short
git fetch origin --prune
git rev-parse origin/develop
git branch -a
git worktree list
git merge-base --is-ancestor e1f3d78 origin/develop && echo "UUID experiment already in develop"
git merge-base --is-ancestor 149d319 origin/develop && echo "UI experiment already in develop"
# 以下仅在本地实验分支存在时运行
git log --oneline origin/develop..experiment/skills-evaluation
git diff --stat origin/develop...experiment/skills-evaluation
```

**决策规则：**
1. 如果没有未合并提交：记录“已包含于 develop，无需操作”，继续本任务。
2. 如果存在本地额外提交：逐项审查、移除陈旧改动、通过构建和回归后，才能合并真正有效的增量到 `develop`；不得直接合并 `main`。如有工作区未提交内容，**不得自动 stash / reset / clean**。
3. 新的架构改造不要直接在 `develop` 上裸改。先新建 `refactor/directory-session-maintenance` 分支和独立 Worktree，在全部 Gate 通过后向 `develop` 提交 PR/合并；**不自动触发生产发布或学生机部署**。
4. 合并动作必须给出合并前后提交范围、测试证据、回滚点与差异清单；遇到实质性冲突或迁移风险时不要硬合并。

---

## 1. 现场证据与已观察到的故障（不能假装已确定根因）

2026-11-05 用户提供四张真实截图：

| 场景                       | 实际看到的情况                                                                                                                  | 当前判断                                                                                                           | 必须查证的证据                                                                                   |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ |
| 安装 StudentSetup `1.1.61` | 安装条已到“正在完成安装”，报 `Runtime error (at 93:1505): 无法安装或启动独立 Maintenance 后台服务`；StudentSetup 主文件仍能运行 | **已确认安装流程存在“文件已部署、服务注册/启动失败”的半完成状态**；不等于服务必然不存在                            | Inno 日志、SCM 状态、service.log、exe 路径、退出码                                               |
| Veyon 原生地点             | 当前只有 **1 台电脑**                                                                                                           | 当前 Veyon 权威目录视角为 1                                                                                        | 地点 UUID、Computer UUID、Host、目录读取时间与指纹                                               |
| “机房座位图”与“学生电脑”   | 显示 **1 个桌位、1 名学生（小子 / PC-01）**，学生电脑显示 1 台且“等待 Agent 回执”                                               | 本地座位与当前设备列表看似一致；不能据此推断 Agent 正常在线                                                        | `SeatId`、绑定 Computer UUID、Veyon 读回、Agent 能力/健康                                        |
| “课堂快捷管控”             | 顶部“待同步 11 台”，逐台执行进度列出 **11 台**；不少显示“未填写姓名、等待上线”                                                  | **强烈怀疑旧活动课堂 Targets / 持久化快捷管控快照被沿用**，而不是最新 Veyon 名单；尚不能断定这 11 台来自哪一份文件 | 当前 `SessionId`、session.Targets、snapshot.Devices、各条 TargetId、Active/Closed 状态、目录版本 |
| “学生端维护”               | Maintenance 已接管 0、Legacy 0、未分类 1、待验证事务 1                                                                          | 只证明本批没有得到可信的 Maintenance 能力分类；**不能直接当作安装失败的证据**                                      | 服务状态、能力 `maintenance-service.v1`、签名信任和网络收发日志                                  |

**必须复现“Veyon 1 台、设备列表 1 台、座位图 1 台、课堂快照 11 台”并找到完整来源链**。不可简单通过 `Clear()`、删 JSON、强制重建课堂来让数字变成 1：那可能丢失仍需解除的课堂限制和延迟回执的安全证据。

### 源码中已明确的相关路径（实际实现前重新定位）

- `src/VeyonCampus.App/TeacherViewModel.cs`：`ClassroomQuickControlProgressHeader` 使用 `_classroomQuickControlSnapshot.Devices.Count`；不是直接读取 Veyon 当前机器数。
- `src/VeyonCampus.App/TeacherWindow.axaml.cs`：打开窗口时读取 `ReadSavedClassroomQuickControlSnapshot(...)`，运行中反复读取 `ReadClassroomQuickControlSnapshot(...)`。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`：快照从 `_classroomQuickControlService.ReadSnapshot(SessionId)` / `ReadSavedSnapshot(context)` 投影。
- `src/VeyonCampus.Core/ClassroomQuickControl.cs`：`ClassroomQuickControlStateStore.DefaultPath` 为本机用户数据下 `VeyonCampus/Teacher/classroom-quick-control.json`，按 SessionId 持久化，包含 `Devices`、`Active`、`ClosedUtc`、状态修订号等。
- `src/VeyonCampus.App/TeacherViewModel.Classroom.cs`：`EnsureClassroomSessionAsync()`、`UpdateActiveClassroomScopeAsync()` 调用 `_classroomSessionStore`；更新范围受验证状态、课堂生命周期和其他 guard 影响。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`：`classroom-sessions.json` 是活动课堂及历史范围的另一份持久化来源。
- `src/VeyonCampus.App/TeacherViewModel.cs`：`HasPendingClassroomScopeChange` 依赖 `ComputerCount`/编号等，不是单纯对比当前 Computer UUID 集合。
- `src/VeyonCampus.App/TeacherViewModel.cs`：`ReadActiveSeatLabels()` 仍使用 `ComputerNumbers`/`Enumerable.Range(...)` + **数组 index** 对应 active targets，存在错位风险。
- `src/VeyonCampus.Core/TeacherCampusDirectory.cs`：`TeacherRoomProfile` 持有 `RoomId`、`DisplayName`、`Prefix`、`StartNumber`、`ComputerCount`，**未持久化 Veyon Location UUID**。
- `src/VeyonCampus.Core/VeyonDeviceDirectoryStore.cs`：已经有基于 Veyon UUID 的设备快照，禁止重复 UUID；应尽可能复用，避免新建第四套权威设备表。
- `src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs`：存在 UUID 读取、只读注册表回退、基于名称的旧投影等多套读法，需要统一信任语义。
- `installer/VeyonCampus-Student.iss`：Maintenance 文件在 StudentSetup 安装包中，复制到独立 `MaintenanceService/Versions/<version>` 目录；在 `ssPostInstall` 调用 `--install-and-start-service`，失败可 `RaiseException`；StudentSetup 主文件此前已部署。
- `src/VeyonCampus.MaintenanceService/Program.cs`：服务名 `VeyonCampusMaintenanceService`；`--verify-running` 返回 0=Running、3=不存在、4=存在但非 Running；注册为 `LocalSystem` 自动启动。
- `docs/releases/v1.1.62.md`：修复误报和旧事务 `PreviousSetupPreserved → Superseded`；**修复说明不是实机验收结论**。近期曾试图把 Maintenance 失败降级为警告，随后因卸载 smoke 回归而撤销；不允许简单吞错。

---

## 2. 根因假设与“名单/状态”完整盘点（先调查再编码）

### 2.1 不要把所有记录统称“名单”

建立数据目录清单 `docs/refactor/data-source-inventory.md`。至少盘点下列 **6 种持久化数据/权威输入** 与 UI 投影，记录文件实际路径、Schema、写入方、读取方、身份键、用途、失效条件、刷新触发和故障处理：

| 数据                                                                   | 权威职责                                                       | **不能用于**                               |
| ---------------------------------------------------------------------- | -------------------------------------------------------------- | ------------------------------------------ |
| Veyon BuiltinDirectory / CLI 读取 / 注册表只读快照                     | 当前 Veyon Location / Computer 对象 UUID、Host、名称与父子关系 | 不代表学生姓名或课堂历史任务               |
| `campus-directory.json` / `TeacherCampusProfile`、`TeacherRoomProfile` | 教师自定义校区/房间及编号规划                                  | 不证明某台电脑目前确实存在于 Veyon         |
| `classroom-seat-grid.json`                                             | 座位位置、学生姓名、SeatId、绑定关系                           | 不自动创建真实设备，不等于 Veyon 完整名单  |
| `veyon-device-directory.json`                                          | **已核验设备目录快照**、冲突标记、绑定状态                     | 不能把陈旧缓存静默当作最新真实设备         |
| `classroom-sessions.json`                                              | 活动课堂、会话范围及课堂历史 Targets                           | 不代表当前选中的房间一定仍有相同设备       |
| `classroom-quick-control.json`                                         | 快捷管控的目标状态、任务/截止时间、释放与恢复凭据              | 不可直接当当前可用电脑列表；也不能随意清空 |

另外盘点网站策略目标缓存、设备健康列表、Maintenance 目标与事务 Journal、身份信任记录，以及手机端投影。**UI 控件集合原则上只是投影，不是新的权威设备名单。** 如果发现未列出的独立名单，补充表格并说明为何存在。

### 2.2 必须回答的具体问题

1. 当前 Veyon 只有 1 台时，“11 台”具体由哪个 SessionId、哪个 store、哪次状态恢复生成？逐个 TargetId 可追溯到什么历史？
2. `classroom-sessions.json` 的旧活动 Session 为什么没有在地点变更后收敛？是没有刷新、刷新失败、校区 RoomId 不一致、Fingerprint 判断不足，还是为了保留旧管控状态而有意不更新？
3. 当前课堂的 **有效执行目标集** 和 **历史待恢复目标集** 是否混在同一个 UI/发送队列？
4. Veyon CSV 导出、`networkobjects dump`、config/注册表快照是否用相同的身份语义；何时视为可信？
5. 在原有 11 台中只有 1 台仍属于当前 Veyon 地点时，那 10 台是安全过期、仍有有效锁定、已经结束但延迟回执，还是未知？必须分别处理。
6. 哪些代码用名称、Host、`ComputerCount`、位置索引推断计算机身份？扫描调用关系，列表到文件与方法级别。

要求提交一张**读/写/状态转移调用图**，区分“实时目录 / 本地档案 / 会话快照 / 安全恢复队列”。

---

## 3. 目标架构：一个真实设备身份源，多个职责清晰的状态层

### 3.1 身份不变量（Invariants）

- **Location 身份 = Veyon Location UUID**；显示名称可变、数量可变。Room 持久化 Location UUID 并支持旧数据迁移；不允许因电脑数量变化把旧 Room 当新 Room。
- **Computer 身份 = Veyon Computer UUID（在 Veyon 目录范围内）**；Host 是通信路由地址，名字是展示属性，电脑编号 `PC-01` 是本地编号标签，StudentName 是座位属性，不能彼此代替。
- **Seat 身份 = SeatId**，座位可以没有电脑，电脑也可以暂时未分配座位；Veyon Count 和 Seat Count **不要求相等**。
- 校区、地点、电脑、座位之间禁止用数组 index 自动对应；必须依靠显式稳定 ID 和经过核验的绑定。
- `VeyonDeviceCount / SeatCount / BoundDeviceCount / ActiveSessionTargetCount / PendingRecoveryCount` 是**不同指标**，UI 和日志不得只写“设备 11 台”而不说明口径。
- 对于特定房间，**新增设备/删除设备/修改名称均不得修改未受影响设备的 UUID、座位 ID 或学生姓名**。
- 只读目录刷新不会意外写入/替换校区或座位档案；写入前必须有明确的事务意图。
- 任何旧会话存在待释放管控时，**不能通过删除快照伪装成功**；旧设备保留在隔离的恢复/告警队列，但不加入当前新课堂的执行列表。

### 3.2 不要引入新的“全能仓库”

尽可能在现有 `VeyonDeviceDirectorySnapshot`、`ClassroomSeatGridStore`、`ClassroomSessionStore`、`ClassroomQuickControlStateStore` 上演进，增加统一的只读目录投影 / Reconciler / 目标解析接口，例如：

```text
Veyon 原始目录（唯一设备身份权威）
              │
      Verified Directory Snapshot
              │
         Device Reconciler
              │
    ┌─────────┼──────────────┐
    │         │              │
座位绑定     当前可执行目标    冲突/未分配设备
    │         │              │
本地姓名     课堂/策略/更新    诊断界面
              │
       Active Session（快照）
              │
   已退出当前范围的旧目标 → 独立 Pending Release/Recovery
```

Reconciler 用纯函数返回 `Bound / Unassigned / Missing / Conflict / StaleSessionTarget` 等可验证状态；不存在“数量相等则同步成功”的逻辑。

### 3.3 配置与数据迁移

当前多个 JSON Store 对未知字段采用 `UnmappedMemberHandling.Disallow`，因此**不能随手给现有 JSON 增加新字段**。设计双向兼容或明确的 Schema 迁移路线：

1. 旧版 Schema 的无损读入与备份；不可破坏原档案。
2. Room Location UUID 只能在唯一、已验证的匹配下回填；多同名地点、重复 Host、UUID 冲突都应 `NeedsReview`，不能猜。
3. 新版格式写盘前准备备份、原子替换和读回验证；失败后保留原文件。
4. 设计旧程序遇到新版 Schema 的回退策略。**不能声称完全向后兼容而旧程序使用 `Disallow` 实际拒读**。
5. 给出独立迁移测试；失败恢复和重复运行均应幂等。

---

## 4. P0：彻底解决“1 台却显示 11 台”与课堂状态漂移

**先写会失败的回归测试，再动生产代码。**

1. 生成测试夹具：Veyon 已验证地点 UUID=L、Computer UUID=C1 一台，Room 有一个 SeatId=S1，旧活动 Session 持有 C1…C11（11 台），旧 QuickControl 快照有 11 条设备状态。
2. 断言当前设备页和当前**新课堂**的执行目标仅为 C1；旧 C2…C11 不得静默发送新指令。
3. 旧 C2…C11 如果有未解除的网站限制/键鼠控制，则进入独立的“历史待恢复 10 台”队列，保留 TargetId、Revision、到期/释放证明；如果已终态，可安全归档。
4. 活动课堂如果范围冻结用于教学连续性，UI 必须明确“进行中的课堂旧范围 11 台（当前 Veyon 为 1 台）”，并在安全切换范围时完成确认与收敛；不能继续显示为“当前 11 台学生电脑”。
5. `UpdateActiveClassroomScopeAsync` 和 `ReadClassroomQuickControlSnapshot` 统一遵守 Room ID、Location UUID、目标 Computer UUID 集合、SessionId、修订号；拒绝旧回执复活/覆盖新范围。
6. 任何刷新失败展示“当前目录暂不可核验 / 上次核验时间”，而不是沿用旧人数作为新人数。
7. 检查 `HasPendingClassroomScopeChange` 的判定，应包括可验证的身份集合变化而非仅 Room `ComputerCount` / Prefix/Number。
8. 用 UUID 映射 `ReadActiveSeatLabels()`，移除基于 index 的虚假对应。
9. `WebsitePolicyTargetChoices`、快捷管控、课堂互动、手机端和 Maintenance 全部消费同一份“当前已验证目标集”投影，避免每处手动拼接 Host。
10. 过期/错误恢复只能在安全边界归档；不得自动删除证书、信任记录、座位姓名或真实 Veyon 对象。

**验收：** Veyon 1、座位 1、学生电脑 1、当前课堂 1；历史 10 如有待恢复需独立说明；重启教师端/切换地点/网络重连后不复活为 11；旧限制到期与解除仍正确。

## 4.5 P0：一次性 Bootstrap、动态配置与程序更新三层解耦

**目标：** 正常功能迭代无需重新生成校区配置包，也无需学生再次手动部署。不要把 Schema 8 当作永久够用的包；不要靠持续新增 Campus Package Schema 来表达每个运行配置模块。

1. **一次性 Bootstrap**：只建立校区身份、固定 Student Agent 身份、初始受信任的配置授权根和首个教师连接。所有受支持的历史 Schema 3–8 都保留安全读取路径；Schema 3 缺失的字段要明确标成“未配置”，不得伪造已有密钥或能力。
2. **独立运行配置模块**：网站/应用/系统策略、TeacherConnectivity 及未来新增模块各自具有 ModuleId、payload schema、独立 revision、fingerprint、签名 role/key、能力要求、Desired/Reported 状态、离线队列和失败恢复。新增模块不得要求提升 Campus Package Schema。
3. **程序发行**：StudentSetup、Agent、Maintenance 的程序文件/版本/协议兼容范围仍由各自签名 release、原子升级和恢复事务管理。程序版本变更不代表模块 Desired 自动应用；模块配置变化也不要求重装程序。
4. **优先复用**：以 Maintenance Protocol `getCapabilities`、签名请求/响应、nonce、防重放、设备身份固定、machine update lock、UpdateJournal、MaintenanceConfigurationReconciliation、已实现的 endpoint recovery 为首选适配点。删除专用 `StudentAgentConfigurationSyncCommand` 或其他旧路径前，要先保留对旧 Agent 的兼容适配器。
5. **Schema 3 向前升级**：验证旧包仅含校区公钥/WebsitePolicy 根时能否先通过已信任签名链更新 Agent 能力，再安全委派模块专用根并推送新增模块；如果某类旧 Agent 无受信任的远程更新能力，界面应明确标为需人工 Bootstrap 修复，不得假报免部署。
6. **信任安全**：不能绕过设备身份 pin、签名验签、purpose/domain separation、revision 和 replay 检查；不能自动替换未受信任或角色不匹配的公钥。新模块根必须由当前已信任授权根签署并绑定模块角色；根轮换需候选 key fingerprint、过渡证明/双签名和设备 Reported 确认，旧根不得提前退休。
7. **事务性**：模块 payload 先完整验证和 staging，再执行原子应用、磁盘/运行健康读回和已验签 Reported；失败时恢复原始配置及模块拥有的系统副作用。模块做不到完整回滚时须进入 NeedsReview，不能声称成功或 InSync。
8. **离线**：Teacher 保存 identity+module 维度的持久 Desired；设备离线不覆盖已生效配置。已认证设备重连后只接收最新有效 revision，重试幂等；签名 Reported 对上 Desired 后才清队列。

### 核心回归

- 旧 Schema 3 校区从已有 Agent 安全升级配置协议；不再重新部署 StudentSetup。
- 新增一个 Schema 3 未包含的配置模块；双方协商后发送模块根委派和独立 payload，未知能力设备明确 Unsupported。
- Teacher 地址/证书更换，Agent 与 Maintenance 双端写入和健康读回；任一步失败都恢复旧配置。
- 学生机离线期间配置改为 revision N+1；重连后只应用 N+1，重复通知/旧签名 N 不回滚。
- 旧/新模块密钥轮换，覆盖旧根授权、新根指纹错误、角色错配、设备未确认和旧根退休边界。

**验收：** 五项合成回归通过，Schema 3 设备不需要手动重新部署即可完成所有其当前 Agent 能力支持的更新；对不支持远程能力的旧 Agent 明确阻断并给出安全修复路径。更新失败不能丢失原始配置或提前丢弃旧信任根。不得在本地验收前改生产环境。

---

## 5. P0：地点与座位数据彻底解耦

- 将 Room 的地点身份由名称/Host 集合匹配迁移为持久化 Location UUID；只允许唯一可信迁移。
- 统一 Veyon 原始读取的 UUID 投影，保留 0 台电脑地点、不同 UUID 的同名地点必须分开并标冲突；不能在按名合并之后才做重复校验。
- 当前 Veyon 电脑数量变化不得重置本机 `campus-directory.json`、`classroom-seat-grid.json`，不得生成另一个同名地点。
- Seat 保存以本地位置/姓名为主；只有需要变更真实 Veyon 对象的操作才写 Veyon；展示名称与实际 Windows 机器名不应被混为一谈。
- 移除日常“同步 Veyon”重复按钮：非破坏性座位编辑保存后自动更新本地投影；创建/删除真实 Veyon 对象必须明确操作边界、读回核验及错误恢复。**自动同步不等于自动删除。**
- 防止 CLI 和注册表回退形成双写源：注册表/配置快照可只读用于展示和校验，但不能绕过 Veyon 官方写入接口直接改注册表。
- 对多机/多进程写入设置互斥、幂等 operation ID、读回验证和必要 journal；原子写本机文件 ≠ Veyon CLI 和多进程之间的 ACID 事务。
- 保留历史场景：原生 Veyon 有 6 台，本机布局有 8 座；有两台无座位，或者有两座未绑电脑；全部应是正常可解释状态，而不是错误。

**验收测试：** 1→7、7→1、仅改名、Host 变化、UUID 变化、同名不同 UUID、空地点、无连续 PC 编号、重复 Host、离线、CLI R6031 只读降级、写操作失败后重试、教师机重启；每次检查所有无关设备的绑定和姓名不变。

---

## 6. P0/P1：Maintenance 服务安装、更新与恢复的单一状态契约

### 6.1 先复现 1.1.61 真机安装失败

- 在干净 Windows VM 与“之前安装失败留有服务注册”的 VM 分别验证 1.1.61 和待验证 1.1.62 构建；不要在真实教室上进行故障注入。
- 收集 Inno 日志、`Get-CimInstance Win32_Service`、`sc.exe qc/queryex`、`C:\ProgramData\VeyonCampus\MaintenanceServiceHost\service.log`、安装目录文件完整性、返回码、服务启动账户、已注册 `ImagePath`、恢复任务。
- 区分：文件已复制 / SCM 未注册 / 注册成功但 Stopped / 服务 Running 但握手失败 / 健康回执无法验签。不要只看异常框或“Maintenance 已接管 0”。
- 安装器当前 `ssPostInstall` 报错确实可能留下“Setup 文件可用、Maintenance 不可用”的状态；建立明确的、可重试的修复入口和真实状态报告。
- **不要简单把服务失败改成 warning**：已有尝试因为半注册服务导致卸载 smoke test 失败而撤销。选择一致的成功/降级/回滚契约，所有分支必须覆盖安装、升级、卸载和重复运行。

### 6.2 安装/恢复不变量

- **正常安装 StudentSetup 时安装 Maintenance**，不是用户完成校区部署才安装。服务路径受管、自动启动、SYSTEM 账户、最小授权、签名证据和版本文件独立。
- 安装完成页不能把“主文件复制成功”称为“完整成功”；显示 `已完成 / 维护服务待修复` 的真实状态和一个“修复”入口（如果设计支持安全的自动修复）。
- 已有健康 Maintenance 不应被普通 StudentSetup 更新停掉或覆盖；Maintenance 自更新需独立受信任的事务与回滚。
- Agent 当前版本、StudentSetup 版本、MaintenanceService 版本、维护协议版本是不同维度；列出兼容矩阵，并为旧 Agent 0.4.55/0.4.56 等建立最小可支持升级路径。
- `PreviousSetupPreserved` / `RollbackPending` / `Superseded` / `Committed` 等状态迁移由单一状态机验证；重复调用或机器重启必须收敛，不得无限“请稍后重试”。
- 维护通道无法建立时，系统应清楚区分 Agent Legacy 兼容通道、需要现场引导安装、需要人工核对和未知状态；不得误报已接管。

**验收：** 全新安装、重复安装、升级、安装器中止、权限拒绝、服务被停止、文件缺失、被旧版本残留占用、卸载回退、系统重启、Agent 离线、Maintenance 通道直连、签名轮换、断电恢复；服务 Running 还要验证 `maintenance-service.v1` 能力和教师端验签回执。

---

## 7. P1：精简页面结构和中文文案（保持功能不删）

### 7.1 UX 方向

针对不懂电脑的教师，**高频操作一步完成，异常才展开技术细节**。本次不迁移 Electron/WebView、不换框架、不整体重做 Avalonia 设计系统。沿用现有设计规范：`docs/ui_rule.md`、`docs/ui_design.md`；避免直接套 Web CSS 示例。

建议将主导航收敛为 **4–5 个一级入口**（先盘点现状、做可审查信息架构草图后实施）：

1. **课堂**：开始/结束、快捷管控、课堂互动、倒计时；当前范围和真实执行结果一眼可见。
2. **设备与座位**：一个入口整合地点、真实电脑、座位布局、学生姓名；使用分栏/页内切换，避免“地点管理”“学生电脑”“绑定同步”重复入口。
3. **策略**：网站黑白名单、系统/应用限制等；明确当前生效范围。
4. **更新与维护**：一个主按钮批量更新；下面是在线状态、进度与需要人工处理的异常；不暴露签名指纹/底层协议等日常无关项。
5. **设置**：教师连接配置、证书/网络、高级诊断、日志和导入导出；危险操作放二级入口。

**这只是目标信息架构，不要为了凑 5 个入口而删除功能。** 保留原路由/事件绑定兼容；分阶段小 diff 改 UI，检查 Win11 缩放与键盘导航。

### 7.2 文案规则

- 一屏只保留：**当前状态、可执行操作、必要结果**。技术原因在“查看详情”/诊断日志中。
- 禁用空话：“已保存期望状态：待同步 11 台”须改成口径清楚的“当前课堂 1 台；历史待恢复 10 台”（仅在事实可证实时）。
- “维护操作已持久化”改为“更新已提交 · 1 台待确认”；展开后才展示事务 ID、协议和签名。
- “尚未收到学生端回执”卡片可显示“未连接”，详情中保留“最后回执时间/失败原因”。
- “请重试 Veyon 同步”不应成为所有错误的统一出口；具体说“Veyon 无法写入”“设备身份冲突”“网络未连接”等。
- 默认用一行人话；长句只在必须提示数据风险时保留。提示一定要有可执行后续动作，不隐藏真正失败。
- 取消无意义的重复选择器和操作按钮；只有一个地点时可直接展示地点名；编辑后应自动刷新本地状态，避免老师手动点一串同步按钮。

### 7.3 UI 验收

使用截图中的四个界面（课堂、设备、座位、安装器告警）作为**前后对照用例**。展示 100%、125%、150% 缩放；验证中文不截断、空列表、1 台/70 台设备、离线、历史待恢复、安装降级状态。视觉审查不能替代功能和安全测试。

---

## 8. 四个 Skills：强制使用，但各司其职

项目已有四个 Skills：`graphify`、`security-audit`、`ponytail`、`impeccable`。**先读取各自 SKILL.md，再按实际支持的模式使用，不得仅在报告中列出名称假装使用。**

| Skill              | 执行职责                                                                                                                                                   | 必须产出                                                                        |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| **graphify**       | 建立 `Veyon → DeviceDirectory → Room/Seat → Session/QuickControl → Policy/Maintenance → UI` 的引用和状态图谱；标记每份名单的写入者与消费者；变更后重扫符号 | 可复现图谱脚本/JSON、Mermaid 图、发现的旧调用点清单；未能静态验证的节点必须标注 |
| **security-audit** | 检查 UUID 身份可信度、Host 绑定/重绑、签名信任边界、Agent/Maintenance 提权、SYSTEM ACL、旧回执重放、文件链接/目录替换攻击、跨进程锁、安装和卸载路径        | 威胁模型、风险等级、关键保护不变量、已验证证据；不在生产环境执行破坏性 PoC      |
| **ponytail**       | 控制代码修改范围；优先纯函数 + 适配层 + 行为测试；识别并删除重复逻辑，避免新增一堆抽象和配置                                                               | 每阶段最小 diff 说明、删掉的重复分支、保留/不修改理由；拒绝一次性全局重写       |
| **impeccable**     | 基于教师工作流做交互审查与文案删减；注意 Web 专用 CSS 指令不能原样应用于 Avalonia AXAML                                                                    | 信息架构前后对照、按钮/文案清单、不同状态截图或可复现设计演示、可访问性检查     |

交叉检查：每个 P0 修复都要有 graphify 数据流证据、security-audit 安全判断、ponytail 最小变更评估；UI 调整必须经过 impeccable 并通过行为回归。

---

## 9. 测试与发布门禁（Gate）：不再逐个症状打补丁

### Gate A：基线与复现

- 锁定当前 `origin/develop` HEAD，保存只读的版本/编译环境/测试状态。
- 在隔离环境复现 1→11 的名单漂移和 Maintenance 安装报错。**如果无法复现，先补可观测性和测试夹具，不要凭猜测重构**。
- 识别已有 `--teacher-workflow-fixtures` 既存失败（实验报告曾显示 `Shared target summary did not refresh after editing computers`、exit 127），修复或以明确理由隔离；不能把默认 90 checks 全绿当作全部测试通过。

### Gate B：组件契约

- Room/Location/Computer/Seat UUID 与 Schema 迁移用例全部通过。
- Veyon 1↔N、删除/添加、离线、重复、无编号、注册表回退、身份冲突的对账纯函数用例通过。
- ActiveSession/QuickControl 状态机覆盖 11→1、历史 PendingRelease、重启恢复、并发异步回执、失效修订号、恢复后不复活旧设备。
- Bootstrap → 动态模块 → 程序发行三层矩阵通过：Schema 3 旧包、Agent capability negotiation、新模块独立 signer/schema/revision、Desired/Reported、离线重连、地址变更双端恢复、信任根轮换；Unknown/旧能力安全拒绝。
- StudentSetup/Maintenance 安装-升级-卸载-恢复的矩阵通过；安装器真实完成状态可核验。

### Gate C：跨模块与真实 Windows

- 编译 `VeyonCampus.slnx -c Release`、应用 AXAML 编译、全部无破坏性的 Checks / fixture；记录每一项测试命令、PASS/FAIL、跳过原因。
- 使用隔离 Windows 10/11 VM、Veyon 4.11.2、本机两台模拟学生机核验：设备名单、座位、课堂、网站策略、维护和更新一致。
- Inno 安装+卸载 smoke 必过；未经过测试不得发布 `StudentSetup`。
- 所有故障注入必须在快照可恢复的 VM；不能覆盖真实教师机 Veyon 目录、Student Agent、证书、校区配置。

### Gate D：UI 及交付

- 对照真实截图验收入口数量、关键流程点击次数、错误文案、键盘操作、Win11 缩放；保留每个旧功能的替代入口。
- PR diff 分阶段、可撤回；每个阶段独立提交。合并前出具迁移备份和回退步骤；不自动给 `main` 打 tag、不发布 CloudBase/GitCode、不向校区批量推送。

### 必须具备的核心回归矩阵

| 用例                                      | 期望结果                                               |
| ----------------------------------------- | ------------------------------------------------------ |
| Veyon 1、座位 1、旧课堂 11                | 当前目标仅 1；历史可能待恢复 10，安全隔离；重启仍正确  |
| Veyon 7、座位 5                           | 2 台未分配设备可见；不自动删除                         |
| Veyon 5、座位 7                           | 2 个未绑定座位可见；不创建伪设备                       |
| 6 台 → 7 台 → 5 台                        | 不受影响的 UUID、SeatId、姓名、策略身份不变            |
| 相同显示名、不同地点 UUID                 | 不静默合并、不误写、明确冲突                           |
| 非 `PC-xx` 的 Windows Host                | 可核验设备身份，不强制改计算机名                       |
| CLI R6031 失败但注册表可读                | 只读可用；写操作安全失败并说明修复方式                 |
| 旧 Session 迟到回执                       | 不能把旧目标重新加回当前课堂                           |
| 旧课堂还有未解除键鼠限制                  | 保留恢复凭据、展示待恢复，不随手清空                   |
| StudentSetup 文件复制成功但服务失败       | 安装/服务状态清晰，修复/回退可完成，卸载不被半注册卡死 |
| 重启后 Maintenance Running、Agent Stopped | 教师端独立验签通道可核验，不能只看 Running             |
| 更新中断在 `PreviousSetupPreserved`       | 正确继续/回滚/安全标记 Superseded，不死循环            |

---

## 10. 实施顺序、提交策略与停止规则

建议按下列阶段**分别提交和验收**，不要将所有修改堆进一个 PR：

- **Phase 0（只读）**：Git 分支核验、源码现状、名单清单、调用图、测试基线、截图故障可复现证据；不改业务代码。
- **Phase 1（P0）**：11→1 的活动课堂/旧状态隔离与目标解析统一；先修当前用户实际故障，并固化测试。
- **Phase 2（P0）**：Location/Computer UUID、Room schema 迁移、Reconciler 和座位绑定；回归保护历史档案。
- **Phase 3（P0）**：Bootstrap、动态配置模块协议与程序更新分层；先将 TeacherConnectivity 作为首个模块完成 Desired/Reported、离线、原子应用与回滚，再迁移未来配置模块。
- **Phase 4（P0/P1）**：Maintenance 安装、服务健康、可恢复更新状态机；真机安装/卸载验证；复用 Phase 3 的能力和事务边界。
- **Phase 5（P1）**：页面信息架构及文案减负；少量页面分批调整，保持事件/命令绑定。
- **Phase 6**：跨模块回归、Windows VM 证据、性能、可观测性、PR/合并与回滚说明。

**停止规则：** 遇到 Schema 破坏性迁移、无法解释的 Veyon 目录写回、旧课堂有未解除限制、服务 ImagePath 指向未知目录、无法读回签名/指纹、非预期 CI 红灯时，停止修改对应生产路径，保留当前数据和可复现证据，不能靠清空或绕过校验继续。

## 11. Codex 最终交付清单（缺任何一项不算完成）

1. `docs/refactor/data-source-inventory.md`：所有名单、状态表、文件/Schema、权威关系。
2. `docs/refactor/root-cause-analysis.md`：对截图四种故障的可复现根因、证据与调用链；确定与尚未确定的结论必须区分。
3. `docs/refactor/architecture.md`：目标结构、身份不变量、Session/Recovery 规则、迁移/回滚设计；并链接 `bootstrap-runtime-update-architecture.md` 的三层更新契约。
4. `docs/refactor/ui-before-after.md`：旧入口→新入口映射，文案删减，截图与可访问性结果。
5. `docs/refactor/security-review.md`：security-audit 威胁模型、已确认风险、缓解和残余风险。
6. 增量产品代码和对应测试：逐阶段提交；禁止未测试的大规模跨模块重写。
7. `docs/refactor/verification.md`：构建命令、全部测试项/CI 状态、Windows VM 安装卸载结果、原始日志路径、失败/跳过项。
8. `docs/refactor/merge-plan.md`：实验分支是否已在 develop、真正剩余的 diff、提交范围、PR/合并建议、数据备份和回退命令。

**完成定义（Definition of Done）**：真实问题消失且已固化为跨模块自动化回归；当前设备人数与旧课堂恢复人数不再混淆；同一设备身份在课堂、座位、策略、维护之间一致；Maintenance 安装和恢复有可信实机证据；教师无需理解内部技术概念即可使用高频操作；原有有效功能和数据保留。**没有跑过的测试必须如实标记“未验证”，不可写“全部完成”。**

---

## 12. 开工确认（Agent 必须先答）

在修改代码之前，先输出一页简短的“执行摘要”：

1. `origin/develop` 实际 HEAD 和本地未提交状态；`experiment/skills-evaluation` 是否有**真正未合并**的增量；如果无则不再合并。
2. 解释“Veyon 1 台 / 座位 1 台 / 当前课堂 11 台”的最可能数据路径，并标出还要查证的存储键和日志。
3. 1.1.61 Maintenance 安装在 `ssPostInstall` 失败的机制、1.1.62 已改什么、为什么还不能宣布修复。
4. 本轮将触及的文件、拟新增/修改的 Schema、风险、测试矩阵、阶段划分。
5. 明确四个 Skills 将实际完成哪些具体审查与产物。

**只要用户没有要求停止，完成摘要后继续 Phase 0 和后续可安全实施的阶段；遇到风险停止规则才需要重新决策。**
