# 已配对手机课堂一对一聊天

日期：2026-10-19（Asia/Hong_Kong）

## 用户需求

除教师电脑和学生 Companion 之间的一对一聊天外，教师也要能从已配对手机选择学生、查看同一条会话并收发消息。

## 交付内容

- 手机“学生互动”页新增支持聊天的学生选择器、单人消息流和输入区；消息按教师/学生区分，显示学生姓名或电脑名、时间和教师消息的学生端送达状态。Enter 发送、Shift+Enter 换行。
- 活动课堂接口返回聊天能力已确认的稳定学生目标 ID 与可用姓名/座位信息。手机专用读写接口仍要求已配对令牌，并在服务端逐次映射、校验请求携带的 Session ID、目标和 Agent 聊天能力；课堂切换或聊天能力撤销会取消对应长轮询。
- 手机发送复用 Teacher 的签名消息路径。手机和 Teacher Console 读取 Teacher 进程内同一线程的最近 200 条消息；消息正文不写入 CloudBase、磁盘、localStorage、IndexedDB 或 Service Worker 缓存。通用课堂事件接口继续过滤聊天正文和送达回执。
- 课堂结束、目标移出课堂或聊天能力撤销时清理对应线程；消息发送重复提交沿用 EventId，避免重复显示。

## 验证与边界

- TeacherConsole Release 构建通过：0 个错误、1 条既有 `TextBox.Watermark` 过时警告。首次使用 `--no-restore` 构建时，因缓存未包含 TeacherConsole 专用 QRCoder 引用而失败；按 TeacherConsole 角色恢复依赖后构建通过。
- `MobileWeb/app.js` 通过 `node --check`，`git diff --check` 通过。本轮未运行自动测试套件。
- 尚未在 Windows 教师/学生双机、390px 浏览器、Android 或 iOS 实测聊天往返、目标隔离、消息失效和本地存储行为；也未完成网络抓包验收。

需求、架构和后续验收见[需求](../../../specs/classroom-one-to-one-chat/requirements.md)、[设计](../../../specs/classroom-one-to-one-chat/design.md)和[任务](../../../specs/classroom-one-to-one-chat/tasks.md)。
