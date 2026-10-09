# 本地课堂会话：设计

## 组件边界

- `VeyonCampus.Core/ClassroomSessions.cs` 提供不可变的校区/机房快照、会话生命周期、随机会话内目标 ID 与本机 JSON 存储。
- 会话使用现有 `TeacherCampusProfile`/`TeacherRoomProfile` 和 `MachineNaming`；不另造一份可编辑教室目录。
- 会话记录保存在 `%LOCALAPPDATA%/VeyonCampus/Teacher/classroom-sessions.json`。文件继承当前用户数据目录 ACL，不进入部署包、CloudBase、手机审计或更新诊断。
- 本轮不增加窗口控件；后续开始/下课入口应使用单一默认流程和当前已选机房，不增加身份、ID 或保存范围选项。

## 数据模型与生命周期

| 对象 | 字段 | 约束 |
| --- | --- | --- |
| `ClassroomRoomSnapshot` | `campusProfileId`、`campusName`、`roomId`、`roomName` | ID 使用本机档案稳定 GUID；显示名是开始时快照 |
| `ClassroomSession` | schema、`sessionId`、Room 快照、UTC 开始/结束、状态、目标数组 | 随机 GUID；仅 `Active → Ended`；结束时间不得早于开始时间 |
| `ClassroomSessionTarget` | `targetId`、`deviceLabel` | `targetId` 随机且只在本 session 有效；标签来自 `MachineNaming` 的电脑编号 |
| 本机文档 | schema、最近会话数组 | 最多 30 条已结束记录及 1 条活动记录，最大 1 MiB |

开始课堂时从现有机房档案快照编号与数量，最多 150 个目标。room/Profile 不存在、范围无效或已有活动会话时拒绝操作。目标清单对外只读；历史会话保留开始时的显示名与目标标签，即使目录之后改名也不回写历史。

结束操作是幂等的：第一次结束记录 UTC 时间；后续重复请求返回原会话，不能延长或重写结束时间。没有活动会话时返回空结果，不创建占位记录。

存储采用严格 schema、有界读取、重复 JSON 字段拒绝、重解析点拒绝及临时文件原子替换。读取失败时不构造空目录回写；开始/结束遇到坏文件时保留原件并返回错误。跨实例写入使用同目录独占锁文件串行化，超时后失败关闭。历史裁剪只在成功解析既有内容后执行。

## 身份、事件与授权

`targetId` 是随机的 session-scoped 路由名，不证明用户、电脑或教师身份。服务端只能在验证 Student Agent 的固定 RSA 身份/校区后，或消费由可信 Agent 针对当前登录会话签发的短时单次凭据后，把连接绑定到一个 target。IP、MAC、请求中的电脑名和 targetId 都不能单独授权。学生登录会话凭据的 IPC 交接由 Student Companion 专项任务落实，本轮不实现该链路。

未来事件信封版本 1 固定为 `{ schemaVersion, eventId, sessionId, targetId?, type, occurredAtUtc, expiresAtUtc, payload }`。`eventId` 用随机 GUID，并在本机会话范围内有界去重；仅活动会话接受新命令。命令最多两分钟后过期；服务端拒绝超出少量时钟偏差的未来时间和下课后的新命令。`targetId` 只做会话内路由，不是认证凭据；学生连接的目标由已认证连接确定，消息正文不得自行选择其他目标。消息/说明长度有硬上限，JSON 拒绝重复和未知字段。事件内容只在 Teacher 与校园 LAN 内学生会话间传递，不进入 CloudBase。

| 事件 | 允许发送者 | 目标范围 | 负载 |
| --- | --- | --- | --- |
| `help.requested` | 已认证学生会话 | 自身 target | 固定原因码 + 最多 300 字符说明 |
| `help.resolved` | 已认证教师 | 单一 target | 求助事件 ID + 固定处理结果 |
| `message.sent` | 已认证教师 | 单一 target 或全班 | 最多 1000 字符纯文本；文本中的链接不自动打开，也不能作为命令执行 |
| `class.session.changed` | 已认证教师 | 当前会话全体 | 固定课堂模式枚举，不包含任意策略内容 |
| `notification.broadcast` | 已认证教师 | 当前会话全体 | 最多 200 字符纯文本 |

事件类型、发送者和收件人共同决定授权；不认识的事件即拒绝，不做“通用命令”回退。session 结束后拒绝新的课堂事件。手机教师权限来自已配对控制服务，桌面教师权限来自 TeacherConsole；两者都必须由服务端判定，事件正文不能自报角色。

## 失败与隐私边界

- 文档 schema/大小/身份异常：拒绝读写并保留原文件；不自动清空或重置会话。
- 活动会话存在：拒绝第二次开始并返回固定可解释结果，不自动结束旧课。
- 并发写锁等待超时：不覆盖另一实例；重试由调用方决定。
- 存储只保留必要元数据，不追加互动正文、设备地址、密钥或稳定设备指纹。
- 教师断网或 CloudBase 不可用不影响本机会话生命周期；本轮并未实现课堂网络服务。

## 验证

- 覆盖房间解析、1/150 边界、随机 ID、姓名/IP/主机名缺席、只读快照及生命周期转换。
- 覆盖活动会话互斥、重复结束、历史 30 条裁剪、原子文件往返、未知/重复字段、损坏/超限文件和重解析点拒绝。
- 运行开发指南规定的 Release 检查入口（当前环境用对应 .NET 检查和角色构建）；Windows 文件 ACL/并发应用实例与课堂 LAN 行为仍需目标机验收。
