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

## 第二轮增量抽取：全局页头

- 将页面标题、当前页面上下文、课堂通知入口、学生消息入口和课堂进行中标记移入 `Presentation/Views/TeacherHeaderView.axaml`。
- 全班通知和聊天按钮仍通过事件转发至 `TeacherWindow.axaml.cs` 中原有处理函数；ViewModel 绑定、课堂会话门控和通知有效期说明保持不变。
- 聊天抽屉关闭、Esc 关闭和遮罩关闭后，焦点仍返回页头的学生消息按钮。焦点目标由新 View 的公开方法提供。
- StudentSetup 角色排除 TeacherHeaderView 的 XAML 与代码文件；学生包不携带教师页面标题及聊天入口。
- 主窗口仍保留单个页面内容滚动宿主，教室/健康/策略/更新的主体尚未逐页抽取，本次没有改变页面切换和滚动行为。

## 当前检查

- 使用 64 位 .NET SDK 10.0.401；解决方案中的 StudentSetup Release 和独立 TeacherConsole Release 构建均成功。
- `scripts/check-app.ps1 -SkipRestore` 完整通过，87 项检查全部通过。测试需要提升权限读取并验证其受保护的 AppData 临时路径；未提升权限的尝试因沙箱拒绝访问该目录而中止。
- `TeacherWindow.axaml`、`TeacherHeaderView.axaml` 和项目文件通过 XML 静态解析；`git diff --check` 通过。
- NuGet 漏洞源不可访问告警及现有 `Watermark`、Avalonia runtime-loader、平台分析器告警仍存在，不是本次引入。
- 双角色 1.1.32 本地测试安装包已构建在 `artifacts/ui03-header-extraction-20261022/`，角色清单分别校验为 StudentSetup 和 TeacherConsole；未发布到远端。包内没有 Developer Release PEM，应用内更新按设计保持停用。
- CUA 当前没有可检查的桌面窗口，本轮未取得真实运行截图。页头的窗口缩放、键盘焦点和聊天入口仍待 Windows 现场验收。

## 检查

- StudentSetup 和 TeacherConsole Release 构建成功；`scripts/check-app.ps1 -SkipRestore` 的 87 项检查全部通过。
- 侧栏 XAML 已由两个角色的 Release 构建解析；`git diff --check` 通过。
- CUA 当前无教师端窗口；键盘焦点、窗口缩放和实际外观仍待窗口运行验收。

## 尚未完成的 UI-03 工作

统一页头尚未拆为独立 View，策略/更新/运维主体也还没有迁移到独立页面宿主。后续只在保留现有绑定与代码后置事件的前提下逐步抽取。
