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

## 第三轮增量抽取：活动页面宿主

- 主窗口滚动区改为 `TeacherPageHost`，按现有导航属性切换地点名单、首次设置、更新、课堂、健康和策略六个页面模板。离开页面后，其页面视觉树不再常驻。
- 六个展示模板仍保存在 `TeacherWindow.axaml`，继承同一个 `TeacherViewModel` DataContext；原有按钮事件继续由 `TeacherWindow.axaml.cs` 处理。
- 座位图拖动处理在地点页面模板加载后连接到当前座位网格；健康告警定位在当前健康页面中查找设备列表。课堂会话、轮询、选项卡、聊天抽屉和滚动宿主继续使用原逻辑。
- 页面模板仍使用原导航状态绑定，策略分类栏留在主窗口页头下方并只在策略页显示。

## 第三轮检查

- 64 位 .NET SDK 10.0.401 下 StudentSetup 与 TeacherConsole Release 构建成功；`scripts/check-app.ps1 -SkipRestore` 的 87 项检查全部通过。
- `TeacherWindow.axaml` 与项目文件通过 XML 解析，`git diff --check` 通过。
- NuGet 漏洞源不可访问、旧 `Watermark`、Avalonia runtime-loader 和平台分析器告警仍在；本次未新增构建错误。
- CUA 当前没有可检查的桌面窗口；页面切换后的尺寸、焦点、座位图拖动和健康告警定位仍待安装包现场验收。

## 当前检查

- 使用 64 位 .NET SDK 10.0.401；解决方案中的 StudentSetup Release 和独立 TeacherConsole Release 构建均成功。
- `scripts/check-app.ps1 -SkipRestore` 完整通过，87 项检查全部通过。测试需要提升权限读取并验证其受保护的 AppData 临时路径；未提升权限的尝试因沙箱拒绝访问该目录而中止。
- `TeacherWindow.axaml`、`TeacherHeaderView.axaml` 和项目文件通过 XML 静态解析；`git diff --check` 通过。
- NuGet 漏洞源不可访问告警及现有 `Watermark`、Avalonia runtime-loader、平台分析器告警仍存在，不是本次引入。
- 双角色 1.1.32 本地测试安装包已构建在 `artifacts/health-alert-stable-20261022/`，角色清单分别校验为 StudentSetup 和 TeacherConsole；未发布到远端。包内没有 Developer Release PEM，应用内更新按设计保持停用。
- CUA 当前没有可检查的桌面窗口，本轮未取得真实运行截图。页头的窗口缩放、键盘焦点和聊天入口仍待 Windows 现场验收。

## 检查

- StudentSetup 和 TeacherConsole Release 构建成功；`scripts/check-app.ps1 -SkipRestore` 的 87 项检查全部通过。
- 侧栏 XAML 已由两个角色的 Release 构建解析；`git diff --check` 通过。
- CUA 当前无教师端窗口；键盘焦点、窗口缩放和实际外观仍待窗口运行验收。

## 第四轮增量抽取：更新与运维页

- 将 `UpdatesPageTemplate` 移到 `Presentation/Views/TeacherUpdatesView.axaml`，包含教师更新、离线安装、学生 Setup/Agent 部署和逐台进度。
- 新 View 继续继承当前 `TeacherViewModel` DataContext；原绑定、显隐条件、进度模板、阶段耗时模板和页面间距保持原样。
- 八个按钮事件由 View 代码后置转发至 `TeacherWindow.axaml.cs` 原处理函数；更新检查、文件选择、身份核对、部署和安装重启逻辑未改动。
- StudentSetup 编译项排除新 View；TeacherConsole 与 StudentSetup Release 构建均成功，0 错误。NuGet 漏洞源、旧 `Watermark` 和 Avalonia runtime-loader 警告仍存在。
- 当前没有可检查的教师桌面窗口；更新页实机布局、键盘焦点与操作入口仍待现场验收。

## 尚未完成的 UI-03 工作

页头和侧栏已独立为 View，六个页面已按需装入活动页面宿主；更新与运维页已抽取，地点名单、首次设置、课堂、健康和策略模板仍在 `TeacherWindow.axaml`。后续继续逐页抽取，并保持当前 ViewModel 绑定和代码后置事件。
