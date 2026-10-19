# Companion 课堂沟通与一对一聊天

日期：2026-10-19（Asia/Shanghai）

## 交付内容

- Teacher Console 在课堂互动区按学生目标选择聊天线程，显示姓名、电脑名、座位位置、未读数、聊天内容和教师消息送达状态。
- Student Companion 将教师回复、课堂通知和一对一私聊放入同一沟通窗口；托盘可主动打开。后台收到消息时窗口显示在右下角，不程序化激活、不改变键盘焦点；学生点击后可正常输入。
- Teacher 按每条教师消息显示 Companion 送达回执；两分钟内未收到回执时提供重试按钮，并沿用原 EventId。教师切换会话或收到当前会话的新消息后滚动到最新消息。
- 私聊复用已有 Agent loopback、Student Agent 签名和 Teacher HTTPS 长轮询；未开放新端口或 WSS。学生消息绑定当前课堂与设备身份，教师消息由校区签名。Agent 必须在签名授权回执中声明 `classroom-chat.v1`；旧 Agent 未声明当前能力或不支持授权端点时不获课堂事件授权，求助、教师回复、通知和私聊均须先更新到当前学生端版本。
- 配对手机读取课堂事件时过滤一对一聊天消息。聊天内容只在 Teacher/Companion 进程内存中保存；课堂结束或进程重启后不恢复旧聊天，服务端聊天事件有效期为两分钟。
- TeacherConsole/StudentSetup 版本为 1.1.7，Student Agent 为 0.4.53、Core 为 0.4.51、Student Companion 为 0.4.67。Agent 版本递增用于让带聊天能力的 Agent 不会被同版本检查跳过。

## 本轮核对与边界

- 静态检查了聊天事件校验、签名授权与回执、Agent loopback 桥接、教师路由和能力校验、手机事件过滤、教师线程界面、Companion poller 与无激活窗口代码。
- 修复教师端聊天能力集合会累积旧授权结果的问题；每次完整授权结果现在会替换当前能力快照。
- 按用户补充的发行范围，移除旧 Agent 降级兼容：能力字段改为必需；旧 Agent 授权失败时教师端提示更新学生端，并清除该设备未确认的课堂事件授权。
- 补上教师消息未确认状态与同 EventId 重试；Student Companion 发送成功状态改为“已提交给教师端”，不暗示教师已读。
- `global.json` 要求 .NET SDK 10.0.100 并允许 `latestFeature`；本机使用 10.0.401 成功构建 StudentSetup、TeacherConsole、Student Agent 和 Student Companion，并生成 1.1.7 离线安装器。StudentSetup 为 63.60 MiB（打包时移除 202.01 MiB PDB），TeacherConsole 为 65.86 MiB（移除 100.98 MiB PDB）。
- 构建报告两条非阻断警告：Companion 沟通窗口没有无参构造函数、教师端使用一处过时的 `TextBox.Watermark` API。Companion 窗口由代码直接构造且 XAML 编译完成；运行时行为尚未实机确认。
- 本轮未运行自动测试套件或启动桌面应用。
- Windows 10/11 双机消息往返、目标离线/旧 Agent、重复/超时重试、下课和重启清理、手机隐私、右下角位置、多屏/DPI，以及 Edge/全屏窗口和键盘焦点仍待实机验收。

## 代码与规格

详细任务及状态见[专项任务](../../../specs/classroom-one-to-one-chat/tasks.md)、[设计](../../../specs/classroom-one-to-one-chat/design.md)和[项目路线 P13](../../开发路线与任务清单.md)。本记录说明的是源码状态，不构成构建通过、发布或现场验收结论。
