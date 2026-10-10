# 课堂倒计时：实施任务

- [x] 1. 实现带范围校验和原子保存的 session 计时模型。
  - 覆盖默认/边界时长、UTC deadline、重启读回、不同 session 隔离及损坏文件保护。
  - _Requirement: 1–3, 5–6_
- [x] 2. 接入 Teacher 活动课堂控件与重启恢复。
  - 默认 15 分钟；不活动课堂隐藏/禁用；运行时显示剩余和结束动作；下课清除当前 session 显示。
  - _Requirement: 1–3, 7_
- [x] 3. 接入配对手机只读状态。
  - 在 `/api/session` 有界返回当前 session 截止时间；PWA 定时刷新显示，不暴露编辑入口。
  - _Requirement: 4–6_
- [x] 4. 补齐 Core/API/Web 检查、使用说明和实现记录。
  - 运行开发指南要求的完整 Release 检查并注明 Windows/手机现场验收边界。
  - _Requirement: 1–7_
- [x] 5. 单独提交此功能。

自动检查和 TeacherConsole Release 构建已完成。Windows 重启/睡眠恢复、触屏显示和真实手机 LAN 验收仍开放；规格勾选不代表现场验收通过。
