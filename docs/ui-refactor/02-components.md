# 阶段 UI-02：基础展示组件

## 本阶段变更

在 `Presentation/Components/TeacherComponents.axaml` 建立纯展示样式，按既有业务绑定展示状态：

- `StatCard`：统一高度、间距和五种语义状态色。
- `StatusChip`：胶囊形，并按成功、关注、危险、信息、未知着色；状态仍通过文字表达。
- `DeviceRow`：稳定的列表行尺寸与姓名/辅助文本层级。
- `EmptyState` / `LoadingState`：统一空态、读取态容器。
- `SideDrawer` 表面样式：供聊天等跨页辅助面板使用。
- `inline-progress`：紧凑读取进度。

设备健康页的五张统计卡已迁移到 `StatCard` 样式。学生部署端继续排除此教师组件文件。

## 设计边界

这些样式只接收现有绑定值和 CSS-like `Classes`，不根据颜色推断业务状态、不发网络请求，也不改变列表刷新或设备状态分类。状态组件必须保留文本标签；后续实际页面迁移时再加入统一线性图标。

## 检查结果

- AXAML / CSPROJ XML 结构检查：相关 7 个文件均可解析；教师主题引用的 6 个 `avares://` 资源路径均存在。
- `git diff --check`：待暂存后执行。
- TeacherConsole / StudentSetup Release 构建与运行预览：当前无 .NET SDK 10.0.100，不能验证。
- 自动化测试、DPI 与长文本视觉检查：未运行，留待应用可启动环境验收。
