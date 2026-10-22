# UI-03 进度：导航与全局页头

## 本次完成

- 侧栏改为 224px 深蓝灰外观，四个常用入口统一为 48px 导航项并配线性图标。
- 手机配对与连接从主导航移入「其他工具」，低频恢复、配置、反馈和帮助仍可从折叠的「遇到问题？」访问。
- 所有页面增加统一页头：显示页面名称、适用的教室/设备上下文，并在已有课堂会话时显示「课堂进行中」。
- 策略页面保留原有分类标签；其目标摘要迁入页头，课堂状态与设备健康页的重复名称/摘要已压缩。
- 页面背景、侧栏和页头使用教师端主题资源；内容边距改为 24px。

## 行为边界

- 导航仍绑定现有 `IsClassroomExperiencePage`、`IsHealthCenterPage`、`IsClassroomPage`、`IsUpdatesPage`、`IsRoomPage` 和 `IsSetupPage`。
- 手机配对、首次设置、名单维护、问题报告、反馈和策略分类事件保持原有处理函数。
- 没有修改课堂状态、轮询、设备选择、通信、策略、安全恢复或持久化逻辑。
- 本次只调整主窗口 shell。主要页面仍在 `TeacherWindow.axaml` 中；页面包含由 `TeacherWindow.axaml.cs` 直接查找的命名控件和事件处理器，逐页抽取需要另立阶段，维持当前事件链。

## 本轮增量抽取

- 已将左侧导航和“遇到问题？”区域移到 `Presentation/Views/TeacherSidebarView.axaml`；主窗口只接收并转发原有五个点击事件，ViewModel、命令和导航绑定仍沿用现有实现。
- StudentSetup 角色从编译项中排除该教师端 View，保持学生 Setup 不携带教师导航。
- 抽取没有改变侧栏样式或尺寸；这是结构调整，不代表本轮已完成视觉验收。

## 检查

- StudentSetup 和 TeacherConsole Release 构建成功；`scripts/check-app.ps1 -SkipRestore` 的 87 项检查全部通过。
- 侧栏 XAML 已由两个角色的 Release 构建解析；`git diff --check` 通过。
- CUA 当前无教师端窗口；键盘焦点、窗口缩放和实际外观仍待窗口运行验收。

## 尚未完成的 UI-03 工作

统一页头尚未拆为独立 View，策略/更新/运维主体也还没有迁移到独立页面宿主。后续只在保留现有绑定与代码后置事件的前提下逐步抽取。
