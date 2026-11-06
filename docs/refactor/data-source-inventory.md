# 数据来源清单（Phase 0 初版）

基线：`origin/develop` / `1a4cfb55afde1d9efe617ea528221e8c4275739c`。本文是源码静态追踪结果，不代表任何 Windows 教师机的实际 JSON 内容或现场目录状态。

## 先回答“到底多少份名单”

按“设备或座位成员会以集合/数组持久化或读取”的口径，当前源码里有 **6 份名单形数据**：

1. Veyon 当前 Location/Computer 目录（外部权威实时目录）。
2. Campus 本机 `campus-directory.json` 的 Room 电脑编号/Host 规划。
3. `classroom-seat-grid.json` 的座位和设备绑定表（允许空座/无座设备）。
4. `veyon-device-directory.json` 的 UUID 核验目录投影（带缺失/冲突历史）。
5. `classroom-sessions.json` 的活动 Session Targets（冻结的课堂执行范围）。
6. 旧版 `classroom-seat-layouts.json` 的座位顺序兼容档案（只在迁移/回退路径使用）。

这 **不等于 6 份都能执行课堂命令**。当前静态追踪确认有 **3 个会产生/承载实际目标成员的集合**：Veyon 当前 Computer 集合、Campus Room 规划集合、活动 Session Targets；但开始课堂从 Room 规划集合生目标，没核对 Veyon UUID 集合。其余为座位/绑定、审计投影或旧版布局。QuickControl 的每设备状态、模式恢复账本、设备健康缓存、网站策略和维护记录是状态/规则，不应计入“设备名单”。

策略/更新页的 `WebsiteLocations` 和 `WebsitePolicyTargetChoices`、PWA `/api/rooms`、PWA 快捷管控响应是内存/UI 投影，不是额外持久名单；但不同页面从不同上游构造，所以显示人数会分叉。

## 名单与状态分层

| 数据 | 默认位置 / Schema | 写入 / 读取 | 身份键与职责 | 不能代表 / 刷新与失效 |
|---|---|---|---|---|
| Veyon Builtin Directory | Veyon 自身配置；`VeyonNetworkObjectDirectory` 完整 CLI 快照 | Veyon 管理器/CLI 写；教师端 `ReadLocations`、`ReadNetworkObjects` 读；读故障时可有注册表只读回退 | Location UUID、Computer UUID、Parent UUID、Host、Name；Windows 当前 Veyon 对象集合 | 不是座位或学生姓名历史；CLI/回退可信等级不同。完整快照带 fingerprint，部分读取不得覆盖可信身份快照 |
| 本机校区/地点档案 | `%LOCALAPPDATA%/VeyonCampus/Teacher/campus-directory.json`；`TeacherCampusDirectoryStore` schema 2 | Teacher UI / `ApplyClassroomVeyonLocation` / `MergeLocation` 写；Teacher 与手机服务读 | ProfileId、RoomId；房间名称、Prefix、StartNumber、ComputerCount/ComputerNumbers；本地命名规划 | 不证明当前 Veyon 仍有这些电脑；无学生姓名，未持久化 Veyon Location UUID。Mac 网络目录由此生成，Windows 课堂 Session 创建也读取此档案 |
| 座位图 | `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-seat-grid.json`；schema 5 | 座位 UI、Veyon 绑定/名称同步写；课堂、PWA、名字 overlay、设备目录投影读 | SeatId、行列、ComputerNumber、StudentName、VeyonObjectId、LocationObjectId、Host | 不创建 Veyon 设备，不决定课堂目标成员；设备可无座，座位可无设备。名称是 Campus 座位主数据，Veyon Name 是另一展示副本 |
| 已核验设备目录投影 | `%LOCALAPPDATA%/VeyonCampus/Teacher/veyon-device-directory.json`；schema 1 | `RefreshClassroom...` 以完整 Veyon 对象快照和座位绑定调用 `Reconcile` 写；策略目标信任检查读 | VeyonObjectId 唯一稳定身份；包含 DeviceId、Host、Name、状态及 StudentName/SeatId 投影；缺失设备保留为 MissingInVeyon | 不是当前课堂名单；缺失历史不能静默作为当前目标。当前 classroom Session 创建未消费其 UUID 集合 |
| 活动/历史课堂 Session | `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-sessions.json`；schema 1 | `ClassroomSessionStore.StartSession/UpdateActiveSession/EndSession` 写；Teacher 启动恢复、课堂上下文、手机服务读 | SessionId；Targets 中为每个目标生成随机 TargetId + DeviceLabel；Room 保存本机编号/数量快照 | 不等于当前 Veyon 目录；目标 TargetId 不是 Veyon Computer UUID。开始时从 RoomProfile.GetComputerNames() 生成，更新/结束需要显式生命周期操作 |
| 快捷管控状态与释放凭据 | `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-quick-control.json`；schema 6 | `ClassroomQuickControlStateStore` 在操作/回执/关闭时写；快照服务读取 | SessionId + TargetId；每设备期望动作、revision、TaskId、期限、状态/释放凭据；ClosedUtc 防止旧事务复活 | 不是当前设备成员名单；展示成员由 Session context 提供。不能为“修人数”清空，否则可能丢失释放凭据 |
| 课堂模式所有权/恢复记录 | `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-mode-state.json`；schema 1 | 课堂模式及恢复代码写/读 | SessionId + TargetId + policy kind/revision/profile；描述需恢复的暂时策略 | 不是当前设备名单；结束前不能因目标离开当前地点就丢弃 |
| 设备健康缓存 | `%LOCALAPPDATA%/VeyonCampus/Teacher/device-health-cache.json`；schema 3，30 天缓存 | Teacher 设备健康采集写，UI 刷新前展示读 | CampusId + Target + StudentName；缓存健康/Agent/服务/策略/更新摘要 | 不是当前设备名单，也不能作为在线证明；只有状态缓存，实际操作目标重建自当前策略选择 |
| 旧版座位顺序 | `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-seat-layouts.json`；schema 1 | `ClassroomSeatLayoutStore` 旧版 fallback 写/读；新 SeatGridStore 仍保留迁移路径 | CampusProfileId + RoomId + TargetOrder/Columns | 只有排序/布局，不是 Veyon 目录或学生主名单；有新版座位图时属兼容数据，不可当新成员来源 |
| 运行时目录与 UI 选择 | Teacher `WebsiteLocations`/`WebsitePolicyTargetChoices`；手机服务 `_classroomTargets`、浏览器 `rooms`/`activeClassroomTargets` | 从 Veyon 当前目录、座位图或活动 Session 投影到内存；请求刷新时重读 | 由上游数据决定；策略与学生更新共享当前 WebsitePolicyTargetChoices | 不是新持久权威名单。手机 `/api/rooms` 与 `/api/classroom/quick-control` 来源不同，不能把两个计数混称当前人数 |
| 课堂倒计时/任务进度 | `classroom-countdown.json`、`classroom-task-progress.json` | Teacher 与手机端服务写读 | SessionId / TargetId 的进度和期限状态 | 属于每目标状态，不能创建或增补设备成员 |
| 本机教室选择 | `recent-room-selection.json` | Teacher UI 写读 | 上次 ProfileId / RoomId | 只记选中项，不是电脑名单 |
| 教师端设备健康 | `device-health-cache.json` | `TeacherDeviceHealthCacheStore` 写读 | Campus + Target 的状态缓存 | 不定义目标，也不证明当前在线 |
| 网站策略规则/预设/历史 | `website-policy-lists.json`、`website-policy-presets.json`、`website-policy-modes.json`、`push-history.json` 等 | 策略页面与策略运行时写读 | 规则、模式、版本、结果；目标字段有时是当次操作快照 | 属于策略/历史数据，不是设备目录；历史目标不得直接作为新课堂名单 |
| Maintenance/更新事务 | `maintenance-runs.json`、学生端 `student-update-transaction.json`、回放/阶段计时与诊断记录 | Maintenance、更新事务及诊断服务写读 | 操作/命令 ID、版本和阶段；部分记录含单机目标 | 是操作 journal，可能保留旧设备/操作信息，不可汇入当前课堂成员 |

以上是本轮已沿源码找到且与名单或其生命周期相邻的持久对象；不把所有 JSON 文件计为名单。完整文件清单受不同目录/平台路径、版本迁移和诊断留存限制，现场仍需读取真实文件才能判断有无陈旧实例。

CloudBase migrations 中查到的表是用户/校区配置、部署包与发布物、心跳/统计遥测、下载控制和下载设置等；没有按学生电脑逐台维护的在线设备名单表。设备活动范围目前是本机 Veyon 目录、教师端本地 JSON 和内存投影。

## 经源码确认的当前读写路径

```mermaid
flowchart TD
  V[Veyon Builtin Directory\nLocation UUID / Computer UUID / Host / Name]
  P[campus-directory.json\nRoom 编号规划 / ComputerCount]
  S[classroom-seat-grid.json\nSeatId / StudentName / UUID 绑定]
  D[veyon-device-directory.json\n核验投影 / Missing / Conflict]
  C[classroom-sessions.json\n活动范围快照 Targets]
  Q[classroom-quick-control.json\n每 Target 状态与恢复凭据]
  M[classroom-mode-state.json\n策略所有权 / 恢复]
  UI1[/api/rooms → PWA 当前电脑名单]
  UI2[/api/classroom/quick-control → Session 快捷管控]
  V -->|Windows 当前读取| UI1
  S -->|学生姓名 overlay| UI1
  V -->|完整 UUID 快照| D
  S -->|座位绑定| D
  D -->|策略目标可信度校验| UI1
  P -->|GetComputerNames / ComputerCount| C
  C -->|TeacherClassroomEventContext| UI2
  C --> Q
  C --> M
  S -->|按 UUID/Host/座位号解析显示名| UI2
```

## 当前确认的重复与差异

1. 三个会影响“执行目标数”的集合互不等价：Windows 当前 Veyon 目标、RoomProfile 生成的目标、已冻结 Session.Targets。另有 3 份名单形数据（座位图、UUID 核验投影、旧布局）承担绑定/显示/兼容用途。
2. `ClassroomSession.Start` 只检查本机 RoomProfile `ComputerCount > 0`，然后用 `GetComputerNames()` 生目标；Windows 的开始 guard 只确认已核验 Veyon 名单数量大于 0，没有验证成员 UUID 集合一致。
3. 读取同名既有 Veyon 地点的分支只报告 Veyon 数量与绑定座位数，不更新既有 RoomProfile 的电脑数量。课堂快照因此可从旧数量生成目标。
4. 课堂快照服务按上下文 TargetIds 枚举逐台设备；快捷管控状态存储并不发明名单，但旧活动 Session 会持续定义当前课堂投影。
5. 因此代码支持 Veyon/设备页为 1，而课堂 Session/逐台快照仍为 11。Windows 端 `campus-directory.json` 与 `classroom-sessions.json` 实际内容尚未读取，不能断言这次 11 的具体来源。
6. `ReadActiveSeatLabels` 通过 ComputerNumber→数组 index→Session target 对应，而非 Veyon UUID；Target 顺序变化时有错绑风险。
7. CloudBase schema/用途文档中没有逐学生电脑名单表；其正式校区、部署包、版本与遥测表不是当前课堂名单源。

## 仍需现场核验

- 活动 `SessionId`、`classroom-sessions.json` 中 11 个 TargetId/DeviceLabel、状态与更新时间。
- 相同 SessionId 在 `classroom-quick-control.json` 是否有 11 条状态，逐个核对 Active/ClosedUtc、Mode、DesiredAction、Revision、TaskId、InputLeaseId/ExpiresUtc、PendingWebsiteRevision。
- C2–C11 是否在 `classroom-mode-state.json` 有未释放策略，以及是否有已验证终态；在确认前保留全部记录。
- Windows 当前 Veyon 位置 UUID、电脑 UUID/Host、目录 fingerprint 和读取时间；座位图 S1 对应的 UUID。

这些现场文件位于教师 Windows 用户 `%LOCALAPPDATA%/VeyonCampus/Teacher/`；当前 macOS 工作区无法证明其现场值。
