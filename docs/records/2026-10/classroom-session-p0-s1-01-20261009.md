# 本地课堂会话 P0 / S1-01 实现记录

日期：2026-10-09

环境：macOS arm64，.NET SDK 10.0.401。

范围：路线图 P0「本地课堂会话模型」与 S1-01「会话、目标和授权设计」。

## 完成内容

- 新增 `ClassroomSession`、`ClassroomRoomSnapshot` 和 `ClassroomSessionTarget`。开始课堂时复用本机校区/机房档案和现有电脑编号规则，生成 1–150 台目标的快照；每堂课的目标 ID 都随机生成且仅在该堂课有效。
- 会话只有 `Active` 和 `Ended` 两种状态，时间归一为 UTC。结束指定 session 是幂等的，重试会返回第一次的结束时间；时间倒置无效。
- 新增 `ClassroomSessionStore`，本机默认位置为 `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-sessions.json`。保留最近 30 堂已结束课堂及最多 1 堂活动课；文件上限 1 MiB，写入采用临时文件、磁盘 flush 与原子替换。重复/未知 JSON 字段、坏数据、超限文件和重解析点会被拒绝，坏文件不会被空状态覆盖；独占锁保护跨实例写入，等待超时失败关闭。
- 会话只存本地校区/机房名称快照、稳定档案 ID、编号范围、电脑编号和时间状态。不会保存学生姓名、IP/DNS、Agent 指纹、消息正文，也没有 CloudBase 或网络调用。
- 新增课堂事件 v1 设计及教师/学生授权矩阵。目标 ID 是路由标识，不是认证凭据；学生目标由经过验证的连接身份绑定。事件限活动课堂、固定类型、短时有效、幂等去重和有界内容；CloudBase 不参与本地课堂事件。

## 实现位置

- Core 模型与存储：[ClassroomSessions.cs](../../../src/VeyonCampus.Core/ClassroomSessions.cs)
- 需求、设计、实施任务：[requirements](../../../specs/classroom-sessions/requirements.md) · [design](../../../specs/classroom-sessions/design.md) · [tasks](../../../specs/classroom-sessions/tasks.md)
- 自动检查：[ClassroomSessionChecks.cs](../../../tests/VeyonCampus.Checks/ClassroomSessionChecks.cs)
- 架构摘要：[veyon_architecture_summary.md](../../veyon_architecture_summary.md)

## 验证

从仓库根目录依次运行：

```sh
/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-restore
/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release --no-restore -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=false
/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release --no-restore -p:VeyonCampusRole=StudentSetup -p:VeyonCampusIncludeInstaller=false
git diff --check
```

- 可移植检查：62/62 通过，包含 150 台边界、会话 ID、私有字段排除、活动会话互斥、结束幂等、30 条历史裁剪、损坏/未知/重复/超限 JSON、符号链接拒绝和写锁冲突。
- TeacherConsole Release：成功，0 警告、0 错误。
- StudentSetup Release：成功，0 警告、0 错误。
- `git diff --check`：通过。

## 尚未验收

本轮没有增加会话 UI、Student Companion、WSS 或消息处理器；没有启动课堂网络服务。目标 Windows 上的用户配置文件 ACL、跨进程实际并发、崩溃写入恢复、教师/学生真实身份交接，以及 10/24/60–70 台 LAN 压测仍待对应阶段实机验收。可移植检查不代表这些实机条件已通过。
