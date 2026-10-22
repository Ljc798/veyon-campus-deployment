# 阶段 UI-01：教师端设计系统

## 本阶段变更

新增教师端专属颜色、间距、字号 Tokens 和共享控件样式：

- `src/VeyonCampus.App/Presentation/Theme/Colors.axaml`
- `src/VeyonCampus.App/Presentation/Theme/Spacing.axaml`
- `src/VeyonCampus.App/Presentation/Theme/Typography.axaml`
- `src/VeyonCampus.App/Presentation/Theme/TeacherControls.axaml`
- `TeacherWindow.axaml` 仅在教师窗口引入共享样式。
- StudentSetup 编译条件排除教师专属主题 AXAML，避免角色资源互相影响。

Tokens 遵循 `docs/ui_rule.md`：浅色工作区、深蓝灰侧栏、青绿选中态、蓝色主操作，成功/关注/失败/未知使用有对应浅底的语义色。主要按钮定义默认、悬停、按下、焦点和禁用状态；现有 `primary`、`secondary`、`tertiary`、`danger` 类名继续可用。

## 行为影响

只在 `TeacherWindow` 作用域加载样式；没有改动 ViewModel、网络请求、状态分类、按钮事件、配对与安全逻辑。学生部署端主题不变。

## 检查结果

- 按 Avalonia 官方样式约定，在窗口级 `Styles` 引入共享 `.axaml`；颜色/尺寸资源通过 `Styles.Resources` 合并。
- AXAML / CSPROJ XML 结构静态检查：涉及的 6 个文件均可解析。
- TeacherConsole / StudentSetup Release 构建：当前环境缺少 `global.json` 指定的 .NET SDK 10.0.100，尚不能验证。
- UI 状态截图：当前 CUA 未列出已运行窗口，本阶段没有运行截图。
- 自动化测试和 Windows 实机验收：未运行。

## 验收边界

这是设计系统地基，不代表四个页面已经改版。后续页面逐步迁移旧样式，避免一次性改变所有控件和绑定行为。
