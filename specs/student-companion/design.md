# Student Companion：设计

## 组件边界

- 新项目 `src/VeyonCampus.Companion` 只引用 Core 与 Avalonia UI；没有 Worker/Agent/TeacherConsole 引用。
- `StudentCompanionViewModel` 接收有限的 `StudentCompanionStatusSnapshot`，只把连接态、教室显示名、课堂模式和目标数量映射成 UI 文案。S1-03 的网络客户端通过同一模型更新状态。
- 默认数据源为“未连接”，不自行探测网络、不读取 Teacher 文件、不访问 Agent 私钥或系统策略。
- StudentSetup 包的 `StudentCompanion/` 子目录包含独立自包含 win-x64 App。安装器创建开始菜单入口和 `{commonstartup}` 快捷方式；快捷方式带 `--startup`，让窗口启动后收至系统托盘。
- Companion 以独立 `asInvoker` manifest 构建。它与 StudentSetup 的管理员 manifest、Agent 的 SYSTEM 计划任务完全分离。

## 交互与视觉

- 默认状态是一张主状态卡：“等待教师端连接”及一句简短说明；不展示不可验证的课堂/电脑数据。
- 连接状态和活动课堂共用同一张卡，不切换到额外设置页；未知状态始终显示为未知/未连接。
- 单击托盘图标打开窗口；关闭窗口只收起；托盘菜单保留“打开课堂助手”和“退出”。启动快捷方式用 `--startup` 自动收起，不抢学生桌面焦点。窗口不再增加重复的“隐藏到托盘”按钮。
- 使用现有 Fluent 主题、品牌图标和柔和背景；不增加学生姓名、输入表单、额外设置或教师管理入口。

## 生命周期与权限

- 程序按交互用户登录启动；无提权 manifest、没有 UAC 操作或持久化服务。
- 单实例限定当前 Windows 登录会话；退出时销毁托盘图标并结束事件循环。
- 安装器不为学生助手创建 SYSTEM 任务或管理员计划任务。卸载删除安装目录和安装器所拥有的快捷方式。

## 验证

- Core/UI 状态检查覆盖默认断开、已连接无课堂、活动课堂、目标数边界和非法状态拒绝。
- 发布脚本检查 StudentCompanion exe/manifest/版本并拒绝把它放进 TeacherConsole 角色。
- 完整解决方案 Release 构建、检查集及 Teacher/Student 角色构建必须通过；Windows 安装器启动项/托盘行为仍需实机验收。
