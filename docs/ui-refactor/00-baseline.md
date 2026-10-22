# UI 重构基线

日期：2026-10-22

分支：`develop`

基线提交：`471123b`（`new feats`）

应用版本：TeacherConsole / StudentSetup `1.1.32`
Avalonia：`12.1.3`
目标 SDK：`.NET 10.0.100`（`global.json`，`latestFeature`）

## 工作区状态

基线检查时没有已修改或已暂存的跟踪文件。以下五个用户提供的文件是未跟踪文件，本次基线和后续 UI 提交均不纳入：

- `docs/ui_design.md`
- `docs/ui_rule.md`
- `docs/image.png`
- `docs/image-1.png`
- `docs/image-2.png`

## 当前界面结构

| 区域 | 当前实现 | 重构方向 |
|---|---|---|
| 主窗口 | `TeacherWindow.axaml` 中约 1,550 行 AXAML；四个主要工作区和辅助配置页共用一个外层 `ScrollViewer`，通过 `IsVisible` 切换 | 先保留现有 DataContext 与事件链，再逐页抽取展示层；不要把业务命令搬进展示组件 |
| 侧栏 | 固定约 244px；课堂、健康、策略、更新为文字导航，手机配对为单独按钮；首次设置、地点名单、恢复和帮助入口位于「遇到问题？」 | 采用 224px 设计宽度、统一图标和状态；低频入口继续可访问，保持恢复能力 |
| 页面上下文 | 工作区顶部只在策略区域显示目标摘要和分类标签；其他页面没有统一的页面标题与上下文栏 | 建立一致的页头，显示当前课堂/校区和本页主要操作，避免重复说明 |
| 课堂控制 | 当前课堂信息、快捷管控、逐台进度、聊天和通知编辑内容在同一长页面中 | 按课堂概览、快捷管控、逐台进度、互动入口组织；聊天跨页抽屉化 |
| 设备健康 | 五项统计、轮询和诊断说明、批量操作、异常卡片、逐台设备列表都集中在一个长卡片/页面中；列表已有本地缓存与稳定行 | 压缩为五张统计卡和一条进度栏；异常优先显示；保留姓名、可信状态、缓存语义与稳定行 |
| 策略与预设 | 网站、应用、系统策略都嵌在主窗口；部分高级输入、候选目录、结果和诊断在同一滚动页面 | 保留三个策略分区和危险操作确认；长说明折叠，高级操作就近收纳 |
| 更新与运维 | 更新和设备运维内容与其他页面共用同一 AXAML/滚动宿主 | 突出版本、兼容性、批次进度和逐台结果；日志按需展开 |
| 手机配对 | `TeacherMobileControlWindow.axaml` 为独立窗口；固定宽度 760、纵向 `ScrollViewer` 和 `StackPanel`，连接二维码、待审批、已连接、帮助、操作记录依次排列 | 保留独立窗口及配对/证书/审批行为；优先重排连接状态、扫码配对、待审批、已配对设备 |
| 样式 | 侧栏、导航、快捷管控、卡片和状态色多在 `TeacherWindow.axaml` 局部硬编码；`App.axaml` 提供 Fluent 基础主题 | 建立集中 Tokens 和共享组件样式；沿用 Fluent，不引入外部字体、图标包或 UI 框架 |

## 视觉与行为基线

- 主窗口声明尺寸为 `1240×880`，最小尺寸 `1024×700`；侧栏 244px，内容区采用约 34px 外边距。
- 页面主背景、侧栏、卡片、状态色在 AXAML 中以多个十六进制常量定义，尚未形成集中资源字典。
- 设备健康列表使用 `ListBox`；课堂进度、告警分组等使用 `ItemsControl`。长列表的虚拟化和外层无界滚动需要在 UI-09 专项核对。
- 聊天当前是课堂页面底部的常驻卡片；输入框已有 Enter 发送和 Shift+Enter 换行相关事件链，重构必须继续沿用该链路。
- 普通版与课堂快捷管控试点版由 `VeyonCampus.App.csproj` 的 `VeyonCampusRole` / `VeyonCampusClassroomQuickControlPilot` 门控；视觉改动不能改变启用条件。
- 本轮用户反馈：健康设备行已经能够持续显示，但「需要处理」告警卡片仍会一闪而过。需要在 UI-05 同时核对告警生命周期和视觉刷新，不能只隐藏告警或改变状态语义。

## 构建与检查

| 检查 | 命令/观察 | 结果 |
|---|---|---|
| Git 基线 | `develop`，`471123b`；跟踪文件工作区干净 | 已确认 |
| TeacherConsole Release | `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusClassroomQuickControlPilot=false` | 未能启动：当前机器没有与 `global.json` 匹配的 .NET SDK `10.0.100`；未产生编译结论 |
| StudentSetup Release | `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release -p:VeyonCampusRole=StudentSetup` | 未能启动：同一 SDK 缺失；未产生编译结论 |
| UI 运行截图 | 本轮 CUA 窗口清单为空，未启动应用 | 无新的运行截图；`docs/image*.png` 是用户提供的设计参考图，不作为当前运行效果证据 |
| 自动化测试 | 未运行 | 本轮目标为 UI 基线与重构；后续如需运行测试再单独安排 |

## 需守住的现有行为

按 `docs/ui_design.md` 与 `docs/ui_rule.md`：课堂会话及固定目标、课堂状态同步、网站策略与 InputGuard 互斥和租约、签名回执与安全解除、设备健康轮询/退避/缓存/异常去重、设备姓名映射与设备身份分离、聊天 Enter/Shift+Enter/中文输入法、长期系统策略、更新事务、手机配对证书/审批/撤销、普通版/试点版门控均属于行为边界。UI 阶段只调整展示和交互布局，不改协议、持久化格式、策略所有权或设备身份。

## 截图与现场验收状态

设计参考图已逐张检查：`image.png` 展示课堂概览、快捷管控和右侧聊天；`image-1.png` 展示手机端课堂与桌面配对工作区；`image-2.png` 展示更简化的手机配对结构。它们是目标方向，不是可直接照搬的运行截图。当前仍缺少教师端四个页面和配对窗口在实际 Avalonia 构建中的同尺寸基线/改版对比，以及 100%/125%/150% DPI、长文本、低配置多设备和真机行为证据。

## 当前阶段结论

结构与行为基线已记录。Release 构建和运行截图受本机 SDK/窗口环境限制，均未验证。恢复可用 .NET 10 SDK 后必须重新构建 TeacherConsole 与 StudentSetup，再继续阶段验收；不能把本记录视为构建或实机通过。
