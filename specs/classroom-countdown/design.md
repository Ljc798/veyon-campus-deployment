# 课堂倒计时：设计

## 数据与生命周期

- Core 使用单份 `classroom-countdown.json`，保存 schema 版本、当前 `SessionId`、UTC 开始和截止时间。默认位置为 `%LOCALAPPDATA%\VeyonCampus\Teacher\`；API 测试使用独立临时目录。
- 仅允许 1–180 分钟，截止时间必须晚于开始时间且不超过 180 分钟。使用严格 JSON、重复字段拒绝、路径链接检查、文件锁及原子替换；损坏文件保留原样。
- Teacher 启动/结束计时只写入/删除当前 session 的状态。Teacher 启动时从本机读回与活动 session ID 相符的状态；其他 session 的状态不展示。
- GET `/api/session` 以可选 `activeClassroomCountdown.deadlineUtc` 暴露只读截止时间。无课堂、无计时或损坏状态都返回 `null`；该可选状态不得使原会话、位置或策略字段失败。
- 计时不会触发策略、课堂模式或其他设备操作；下课后 API 按 session ID 隔离旧状态。

## UI

- 不新增页面。Teacher 在现有活动课堂卡片中设置分钟并启动；默认 15 分钟。运行后只显示倒计时和“结束倒计时”。
- Teacher 用 UTC 截止时间计算显示值，不累计 DispatcherTimer tick，避免电脑睡眠造成漂移。UI 定时刷新只是显示。
- 手机 PWA 在课堂模式卡片中显示当前剩余时间，不提供编辑和操作入口。会话轮询刷新截止时间，页面计时器以浏览器当前时间更新显示。

## 验证

- Core：时长边界、deadline/remaining、session 隔离、持久化恢复、损坏文件保护。
- API：活动课堂返回有效截止时间，无活动课堂和损坏文件返回 null 且 `/api/session` 仍成功。
- TeacherConsole Release 构建、完整 .NET 检查和 MobileWeb JavaScript 语法检查；Windows 睡眠/重启与真实手机 LAN 显示仍需实机验收。
