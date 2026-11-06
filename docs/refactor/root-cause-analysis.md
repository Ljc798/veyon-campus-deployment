# 根因分析（Phase 0；现场根因待核验）

基线 `origin/develop` = `1a4cfb55afde1d9efe617ea528221e8c4275739c`。本轮未读取真实 Windows 机上的 `%LOCALAPPDATA%`、SCM 或安装日志；截图只证明界面状态，不证明具体本机文件值。

## 1. Veyon 1 / 座位 1 / 课堂 11

### 已确认的代码行为

- Windows 当前目标设备页/手机目录经 `TeacherNetworkDirectory.ReadLocations()` 读取 Veyon；手机 `GET /api/rooms` 独立读取当前地点并以座位图叠加姓名。
- `ClassroomSession.Start()` 从本机 `TeacherRoomProfile` 的 `ComputerCount` 与 `GetComputerNames()` 生成 Session.Targets；每个目标拿到随机 TargetId，不绑定 Veyon Computer UUID。
- Windows 开始课堂 guard 只要求当前已验证 Veyon 目录目标数大于 0，没有把当前地点 Computer UUID 集合与 RoomProfile 生成集合相等核验。
- 已存在的同名本机 Room 配对 Veyon 地点时，`ApplyClassroomVeyonLocation()` 分支只报告 Veyon 电脑数量和已绑定座位数，不更新 RoomProfile 数量/成员。
- 快捷管控 `BuildSnapshot()` 以 `TeacherClassroomEventContext.TargetIds` 生成逐台列表；context 从活动 Session Targets 解析。因此旧活动 Session 可使当前快照继续显示 11。
- `ReadActiveSeatLabels()` 以 ComputerNumber→数组 index→Session.Targets 关联座位，不使用 Veyon UUID；目标排序/编号变化时存在错误映射风险。
- `ClassroomSession.UpdateScope()` 通过 `DeviceLabel` 重用旧 TargetId；因此同一个 PC 编号在 Veyon 中被删后重建成新 Computer UUID 时，旧会话身份可能被沿用。QuickControl 源码注释称 TargetId 是 room-directory identity，但当前 `ClassroomSession.Start()` 实际为每次会话随机生成它；注释与实现不一致。
- `HasPendingClassroomScopeChange` 只比较 Room ID、名称、Prefix、编号/数量，不比较 Veyon Location UUID 或 Computer UUID 集合。因此 Veyon 设备集从 11 变 1，但本机 Room 规划字段不变时，变更检测本身不会发现成员变化。

### 当前最可能解释（尚非现场定案）

截图中的 1 台来自 Veyon 当前地点投影；11 台来自已持久化 Session Targets，可能由旧 RoomProfile 的 ComputerCount 生成，也可能是当前 Veyon 收缩前开始、仍处于 Active 的旧 Session。现有源码允许两者发生。仅凭截图无法判断是哪一个 SessionId/哪一次持久化写入形成 11。

### 必须补齐的最小现场证据

只读采集教师机 `campus-directory.json`、`classroom-sessions.json`、`classroom-quick-control.json`、`classroom-mode-state.json`；核对同一 SessionId 的 Targets、TargetId、Active/ClosedUtc、UpdatedUtc、每个状态的 DesiredAction/Revision/TaskId/Mode/ExpiresUtc 及策略所有权。再采集同一时点 Veyon Location UUID、Computer UUID/Host 与目录指纹。对每个旧目标分类为仍需解除、已安全终态、迟到回执待验或未知。未知/待解除记录必须保留。

## 2. StudentSetup 1.1.61 Maintenance 后置失败

### 当前代码已确认

StudentSetup `ssPostInstall` 在主安装动作之后执行 `InstallAndStartMaintenanceService()`；函数先调用版本目录的服务控制程序注册/启动，再最多 12 秒轮询 `--verify-running`。最终仍失败时 `RaiseException`，显示“无法安装或启动独立 Maintenance 后台服务”。`MaintenanceServiceTouched` 在操作开始前即设为 true，后续 Setup/Uninstall rollback 必须按此状态处理。当前代码没有把运行健康的服务失败静默降级为 warning。

`docs/releases/v1.1.62.md` 声称修复 START_PENDING 误报、退出码日志和卸载时 ImagePath 残留回退；这些是变更说明，不是 Windows 安装/卸载实测证据。当前代码没有提供本机运行结果或 Windows SCM 状态，因此不能认定 1.1.62 已实机修复。

### 未确认

截图对应机器的安装器日志、`Win32_Service`/SCM 记录、ImagePath、Maintenance `service.log`、服务目录文件、control/state 返回码和实际 capability/验签收发均缺失。不能判断属于注册失败、启动失败、旧 ImagePath、权限、服务运行但能力/信任失败中的哪一种。

## 3. 新旧状态是否混合

QuickControl 快照展示成员取自当前 context Session Targets；QuickControl Store 保存每个 TargetId 的操作、期限、revision 与释放凭据。恢复列表从 Session 历史和 ClassroomModeStateStore 所有权账本关联而来。设计意图是保留历史恢复凭据，但当前 UI 是否把活动课堂范围和旧待恢复目标明确分层，需继续查 AXAML、手机 API、状态转换和旧 Session 加载/范围切换调用。

## 4. Veyon 读取可信语义

源码有 CLI `networkobjects dump/export`、注册表只读回退、普通 locations 投影、UUID 对象快照与 fingerprint 检查；已核实策略写入要求完整新鲜目录并校验目标无冲突。但课堂 Session 目前使用 RoomProfile 目标生成而非 UUID snapshot。不同读取路径何时产生完整/部分数据，需画调用图并针对并发目录变化/失败回退补测试。

## 5. 基线检查暴露的独立测试缺口

- Release build 通过，但有 238 条警告；详见 `verification.md`。成功编译不等于 Windows 安装/运行验证。
- 默认 Checks 在第一个失败点中止。非 Windows 分支断言期待 `请在 Windows 教师端设置学生电脑限制`，而当前 `GetStudentSystemPolicyPushGuidance()` 没有操作系统平台判断，也没有该提示。源码和测试契约不一致：要么 UI 必须在非 Windows 禁用并说明原因，要么该平台分支断言过期。应明确产品约束后增加独立测试，不能忽略这个失败。
- `--teacher-workflow-fixtures` 中四项通过后在 `TeacherWorkflowChecks.cs:57` 失败。该复合断言同时要求摘要通知、2 个目标和新加入目标全选。setter 会发摘要通知，新目标选择状态初始化为 false；这更像夹具依赖旧的“默认全选”行为，而非课堂 1→11 的复现。先拆成三条契约，再确认目标选择默认值；不得把此项归为环境失败。

## 6. Phase 0 尚未完成的验证

- 当前 Windows LocalAppData 文件/SessionId/目标逐条证据：未取得。
- StudentSetup 1.1.61/1.1.62 Windows VM 安装、升级、卸载及服务能力回执：未验证。
- 默认 Checks 仅执行至第一个失败；其余 checks 未运行。
- `--teacher-workflow-fixtures` 已复现历史失败；当前原因仍待拆分断言/确认产品选择语义。
- 本地 CloudBase API contract checks：基线 22/22 通过（node v24.16.0；不代表桌面代码、真实 CloudBase 或 Windows 通过）。
- graphify AST：822 个代码文件；25 个 SQL 因缺少 `tree_sitter_sql` 未解析，AXAML 未被代码 AST 支持；1 个 Program.cs 报语法解析失败。不能把图谱说成完整调用证明。
