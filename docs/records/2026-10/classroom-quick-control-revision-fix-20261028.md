# 教师端课堂策略状态回退排查与版本栅栏修复

日期：2026-10-28
基线：TeacherConsole 1.1.40，工作区基线提交 `50508ec`
范围：课堂快捷管控 Apply、Restore、自动截止、离线补同步、重启恢复。

## 代码证据与根因

对照 1.1.40 基线检查 `TeacherClassroomQuickControlService` 和 `ClassroomQuickControlStateStore`，确认存在两条可导致状态回退的路径：

1. Apply 会沿用持久化的旧 `ControlExpiresUtc`。自动截止定时器继续观察该时间，到期后提交 Release。因此重新应用后设备会短暂恢复限制，旧截止时间到达时又被自动恢复；若一批设备共用该截止时间，就可能逐步都显示需要恢复。这是与现场描述吻合、已由基线源码确认的主要根因。
2. 设备状态保存没有按操作版本和状态版本拒绝迟到写入。Apply/Restore 网络请求期间启动的旧任务，或基于网络请求前快照产生的状态轮询结果，可以在较新操作后写回本地记录，导致“正在恢复”、旧策略阶段或旧设备状态覆盖最新结果。

还检查了独立的旧课堂待恢复入口：它也可能为同一台设备发网站恢复策略，但会先核对旧策略归属 Revision，推送时携带期望 Revision；Agent 在串行 Apply gate 内再比较当前 Revision。它是需要纳入日志排查的并发写入来源，但按现有比较交换逻辑，旧版本恢复不能覆盖已经接受的新策略版本，未发现它是本次回退的直接根因。

以上是源码路径确认，不是现场运行日志确认。本工作区没有对应的 Windows Teacher/Agent 运行日志或 Teacher 持久化状态文件，尚不能把某台设备的实际回退时间与任务逐条对应。需要用下方事件日志在修复版现场复现或排除这些路径。

## 修复内容

- `ClassroomQuickControlStateStore`（schema 6）是教师端课堂快捷管控的持久化权威状态。每台设备的 `DesiredRevision` 跨课堂单调递增，`TaskId` 标识该次期望操作，`StateRevision` 对设备状态写入做版本栅栏；开始操作前先将目标动作和新的截止时间原子写入，再发网络请求。
- 每台设备串行执行冲突操作。网络阶段及回执回写前再次核对当前 `DesiredRevision`、`TaskId` 和课堂状态；旧任务以及基于旧状态快照的轮询写入被拒绝，并记录为 `stale-task-rejected`。多课堂同设备接管时，旧课堂任务被标记为已接管并失效。
- 每次新的 Apply 使用新截止时间，自动截止任务需匹配仍有效的截止时间才能创建 Restore。关闭课堂会持久化 `ClosedUtc`，只为已有策略归属创建恢复任务，并取消尚未落地且无设备归属的 Apply；关闭后的会话不能重新创建 Apply/旧任务。重启时暂停旧进程中的在途任务，恢复活动课堂时按最新持久化期望状态核对。
- 离线或暂时无法取得可信状态时，设备保留待同步状态。同一操作沿用相同 Revision 和 TaskId，以 15、30、60、120、240、300 秒退避重试；5 秒补同步循环只检查已到期的重试项，不会每轮重新创建任务。
- 网站策略只有在 Agent 回执通过身份、签名、请求随机数和策略正文校验，并且后续签名状态读回匹配目标 Revision/模式后，才确认策略已应用或已恢复。无法确认时保留 `Unknown`/待处理状态，不报成功。教师端关闭后，旧 schema 的网站策略状态迁移为 Unknown 并要求重新读取 Agent 签名状态。
- Apply、Restore、到期、重试、回执、轮询和拒绝旧写入均记录来源与原因。网站策略 Agent 端通过串行 Apply gate、`ExpectedRevision` 比较交换和单调策略 Revision 拒绝旧策略；教师端本地 `TaskId` 不直接传给网站策略 Agent，但 Agent 策略版本、签名请求回执和签名读回承担设备侧顺序与可信确认。InputGuard 使用 `CommandId`、nonce、预期控制 epoch 和签名回执，拒绝重复命令及 epoch 已变化的命令。

## 事件日志

默认路径：

- Windows：`%LOCALAPPDATA%\VeyonCampus\Teacher\classroom-quick-control.json.events.jsonl`
- macOS：`~/Library/Application Support/VeyonCampus/Teacher/classroom-quick-control.json.events.jsonl`

每行是一个 JSON 状态变更，包含 UTC 时间、会话与设备 ID、任务 ID、期望 Revision、网站策略 Revision、旧状态、新状态、来源、触发原因、是否接受、课堂状态 Revision 和设备状态 Revision。被拒绝的迟到写入以 `accepted: false`、来源 `stale-task-rejected` 记录。日志采用追加写入，最大保留约 2 MiB；达到上限时保留尾部记录。

## 验证与验收边界

- 代码验证：TeacherConsole Release 构建成功，0 warning、0 error；`git diff --check` 通过。
- 未运行测试套件；未取得修复版 Windows 现场日志或设备，以下验收尚未完成：Apply → Restore、自动截止、教师断网/学生机离线后重连、教师端重启、关闭课堂后迟到回执，以及上述场景的事件日志顺序检查。
- 当前修复是本地源码工作区变更；没有生成或发布新版本包。TeacherConsole 1.1.40 发布物未更改。现场验收需使用包含该修复的后续构建，并保留事件日志用于确认实际任务顺序。
