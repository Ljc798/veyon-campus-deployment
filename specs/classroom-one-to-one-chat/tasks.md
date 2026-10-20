# 一对一课堂聊天：实施任务

更新日期：2026-10-19。`[x]` 表示对应源码路径已实现；不表示构建、自动检查或 Windows 现场验收通过。

## 代码实现

- [x] CHAT-01 Core 定义双方聊天事件、送达回执、确定性课堂/目标对话 ID、长度/有效期/去重及有界队列；聊天事件按 2 分钟有效期清理。
- [x] CHAT-02 Student Agent 复用 39174 端口和已有签名授权；Companion 仅经 loopback 提交正文，由 Agent 注入当前 session/目标并签名。
- [x] CHAT-03 Agent 的已签名授权回执必须声明 `classroom-chat.v1`；旧版缺少能力字段、缺少当前能力或不支持当前授权端点时拒绝课堂事件授权，并提示更新学生端。
- [x] CHAT-04 Teacher HTTPS 接收、验证和路由私聊事件；教师聊天能力绑定当前课堂与逐台回执，手机通用课堂事件页继续排除一对一私聊内容。
- [x] CHAT-05 Teacher Console 在课堂互动区按学生选择会话，显示学生姓名/电脑名/座位信息、未读数、连续消息和输入框；教师消息两分钟未收到 Companion 回执时显示未确认状态并可用同一 EventId 重试。
- [x] CHAT-06 Student Companion 提供与课堂通知共用的沟通窗，学生可从主窗口或托盘主动联系老师，并查看消息历史、发送状态和原 EventId 重试；后台新消息显示窗口时不激活、不抢键盘焦点。
- [x] CHAT-07 会话仅保存在 Teacher/Companion 进程内存；下课或进程重启后不恢复旧聊天，Companion 启动时忽略之前发出的聊天消息。
- [x] CHAT-12 教师消息项显示当前学生姓名（缺失时显示电脑名）；教师端和 Companion 输入框都支持 Enter 发送、Shift+Enter 换行，协议校验仅额外允许换行字符。
- [x] CHAT-13 去除与单条消息状态重复的聊天成功提示；教师收到学生消息时保留姓名和时间，不再重复显示“学生消息”标签。
- [x] CHAT-14 已配对手机可在活动课堂选择聊天能力已确认的学生，读取 Teacher 进程内同一线程最近 200 条消息、发送消息并查看学生端送达回执；API 同时绑定请求 Session ID 和稳定目标 ID，私聊响应仅含单一目标，聊天正文不写入浏览器磁盘存储。

## 构建与运行验收

- [x] CHAT-08 在具备仓库要求的 .NET SDK 环境构建 TeacherConsole、StudentSetup/Student Agent 和 Student Companion；核对版本、XAML 编译、发布载荷与当前 Agent 能力声明。2026-10-19 使用 .NET SDK 10.0.401（`global.json` 允许 `latestFeature`）完成双角色 Windows x64 Release 发布和角色载荷隔离核对；Companion 保留 AVLN3001，Teacher 保留旧 `Watermark` API 警告。此前生成的 63.60/65.86 MiB 离线 EXE 早于本轮原子更新源码，本次 macOS 环境没有重建 Inno 安装器；不能把旧 EXE 当作包含原子更新修复的当前发布包。详见[P13-06 发布载荷核对记录](../../docs/records/2026-10/P13-06发布载荷核对-20261019.md)。
- [ ] CHAT-09 使用教师机、学生机和已配对手机验证教师/手机/学生任一方先发、多轮连续聊天、跨端同步、重复提交、超时重试、离线提示、旧 Agent 拒绝及更新提示、课堂结束和重启清理。
- [ ] CHAT-10 Windows 10/11 验证收到消息时 Companion 沟通窗显示在右下角但 Edge/全屏应用及键盘焦点保持不变；点击窗口后可输入，并检查多屏、DPI、托盘启动和关闭收起行为；在 390px、Android、iOS 上验收手机聊天选择、收发、滚动和安全区。
- [ ] CHAT-11 确认配对手机只能读取当前选中的单一学生线程，通用课堂事件页不含私聊；确认网络抓包仅使用既有端口，聊天正文不写磁盘、不进入 CloudBase。

## 2026-10-19 当前执行边界

- 源码版本：TeacherConsole/StudentSetup 1.1.7；Student Agent 0.4.53；Core 0.4.51；Student Companion 0.4.67。
- 本机使用 .NET SDK 10.0.401，符合 `global.json` 的 `latestFeature` 规则。此前学生端和教师端离线安装器分别为 63.60 MiB 和 65.86 MiB；这些 EXE 早于 2026-10-19 原子更新改动。最新源码已完成两个角色的 Release 发布目录核对，但需在 Windows 上重新生成 Inno 安装器后才能用于更新验收。
- 构建输出有两条非阻断警告：Companion 沟通窗口没有无参构造函数，因此不可通过 Avalonia runtime loader 反射实例化；教师端一处 `TextBox.Watermark` API 已过时。窗口由应用代码直接构造并完成 XAML 编译，但仍需现场确认。
- 本轮未运行自动测试套件，也未在 Windows 桌面执行双机、非抢焦点、手机隐私或网络验收；这些现场任务仍待完成，项目状态见[路线主表](../../docs/开发路线与任务清单.md)。

## 2026-10-19 配对手机支持增量

- 已配对手机现可从活动课堂中选择聊天能力已确认的学生，读取同一条会话、发送消息并查看 Companion 送达回执；学生从 Companion 主动发起的消息也进入手机线程。
- 手机发送通过现有 Teacher 签名路径；专用接口用配对令牌和 Session 内稳定学生目标 ID 校验，只返回当前目标最近 200 条内存消息。通用课堂事件页仍排除私聊，正文不写入浏览器存储、CloudBase 或磁盘。下课、目标移除或聊天能力撤销时清除对应线程。
- TeacherConsole Release 构建成功，0 个错误、1 条既有 `TextBox.Watermark` 过时警告；`MobileWeb/app.js` 通过 `node --check`，`git diff --check` 通过。本轮未运行自动测试套件。
- Windows 教师/学生双机和 390px、Android、iOS 实机交互、消息隔离与磁盘/网络验收仍未完成，见 CHAT-09 至 CHAT-11。

## 2026-10-20 私聊自动检查补充

- `VeyonCampus.Checks` Release 检查共 79/79 通过；新增 Companion 草稿重试和 Teacher/学生/配对手机聊天 API 检查，提交 `d6104e5`。
- API 检查覆盖教师消息重试去重、相同 EventId 更换正文时拒绝、学生签名消息和重复提交、送达回执更新、Session/学生目标绑定、私聊只进入所选聊天线程且不出现在普通课堂事件页。
- Companion 检查确认重试成功后会清空原消息草稿，同时保留用户在等待期间输入的新草稿。
- 当前执行环境是 macOS arm64；本轮 CUA 原生桌面启动失败，未取得 Windows 教师/学生双机、Android/iOS 设备或网络抓包证据。CHAT-09 至 CHAT-11 继续保持未完成。
- iPhone 15（iOS 17.5）Simulator 的 Safari/WebKit 在 390 CSS 像素视口检查了学生选择、师生消息、输入框和底部导航排版；数据由本机合成 API 提供，只是模拟器视觉证据，不能替代真实 iPhone/Android、键盘和校园网验收。
