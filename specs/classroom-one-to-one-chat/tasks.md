# 一对一课堂聊天：实施任务

更新日期：2026-10-19。`[x]` 表示对应源码路径已实现；不表示构建、自动检查或 Windows 现场验收通过。

## 代码实现

- [x] CHAT-01 Core 定义双方聊天事件、送达回执、确定性课堂/目标对话 ID、长度/有效期/去重及有界队列；聊天事件按 2 分钟有效期清理。
- [x] CHAT-02 Student Agent 复用 39174 端口和已有签名授权；Companion 仅经 loopback 提交正文，由 Agent 注入当前 session/目标并签名。
- [x] CHAT-03 Agent 的已签名授权回执必须声明 `classroom-chat.v1`；旧版缺少能力字段、缺少当前能力或不支持当前授权端点时拒绝课堂事件授权，并提示更新学生端。
- [x] CHAT-04 Teacher HTTPS 接收、验证和路由私聊事件；教师聊天能力绑定当前课堂与逐台回执，配对手机事件页不返回一对一私聊内容。
- [x] CHAT-05 Teacher Console 在课堂互动区按学生选择会话，显示学生姓名/电脑名/座位信息、未读数、连续消息和输入框；教师消息两分钟未收到 Companion 回执时显示未确认状态并可用同一 EventId 重试。
- [x] CHAT-06 Student Companion 提供与课堂通知共用的沟通窗、托盘入口、消息历史、发送状态和原 EventId 重试；后台新消息显示窗口时不激活、不抢键盘焦点。
- [x] CHAT-07 会话仅保存在 Teacher/Companion 进程内存；下课或进程重启后不恢复旧聊天，Companion 启动时忽略之前发出的聊天消息。
- [x] CHAT-12 教师消息项显示当前学生姓名（缺失时显示电脑名）；教师端和 Companion 输入框都支持 Enter 发送、Shift+Enter 换行，协议校验仅额外允许换行字符。

## 构建与运行验收

- [x] CHAT-08 在具备仓库要求的 .NET SDK 环境构建 TeacherConsole、StudentSetup/Student Agent 和 Student Companion；核对版本、XAML 编译、发布载荷与当前 Agent 能力声明。2026-10-19 本机使用 .NET SDK 10.0.401（`global.json` 允许 `latestFeature`）完成双角色发布和离线安装器打包；XAML 编译通过，保留 AVLN3001 和旧 `Watermark` API 两条警告。
- [ ] CHAT-09 使用教师机和学生机验证教师先发、学生先发、多轮连续聊天、重复提交、超时重试、离线提示、旧 Agent 拒绝及更新提示、课堂结束和重启清理。
- [ ] CHAT-10 Windows 10/11 验证收到消息时沟通窗显示在右下角但 Edge/全屏应用及键盘焦点保持不变；点击窗口后可输入，并检查多屏、DPI、托盘启动和关闭收起行为。
- [ ] CHAT-11 确认手机端不能读取私聊消息；确认网络抓包仅使用既有端口，聊天正文不写磁盘、不进入 CloudBase。

## 2026-10-19 当前执行边界

- 源码版本：TeacherConsole/StudentSetup 1.1.7；Student Agent 0.4.53；Core 0.4.51；Student Companion 0.4.67。
- 本机使用 .NET SDK 10.0.401，符合 `global.json` 的 `latestFeature` 规则。学生端和教师端离线安装器分别为 63.60 MiB 和 65.86 MiB；打包时去掉 15 个学生端 PDB（202.01 MiB）和 6 个教师端 PDB（100.98 MiB）。
- 构建输出有两条非阻断警告：Companion 沟通窗口没有无参构造函数，因此不可通过 Avalonia runtime loader 反射实例化；教师端一处 `TextBox.Watermark` API 已过时。窗口由应用代码直接构造并完成 XAML 编译，但仍需现场确认。
- 本轮未运行自动测试套件，也未在 Windows 桌面执行双机、非抢焦点、手机隐私或网络验收；这些现场任务仍待完成，项目状态见[路线主表](../../docs/开发路线与任务清单.md)。
