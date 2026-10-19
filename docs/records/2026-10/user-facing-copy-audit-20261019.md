# 用户界面文案精简记录

日期：2026-10-19

## 调整

- 手机端将重复的连接状态改为“当前设备”，把第二个“学生互动”区块改为“课堂消息”，删去重复的品牌/页面标题。
- 屏幕监看说明合并重复的可见画面信息；保留先选座位电脑并开启课堂的前置条件，以及离页/下课后停止更新的说明。
- 教师聊天成功状态由单条消息保留；重复的状态栏成功提示、聊天标题中的重复学生姓名和通道状态副本已移除。教师收到学生消息时显示姓名与时间，不再重复标“学生消息”。学生端保留消息气泡中的提交状态和发送失败原因；窗口正文不再重复系统标题栏的“课堂沟通”。
- 手动 SID 提示合并为单段，明确仅支持单台、会覆盖上方账户、系统策略仍按电脑选择账户，以及仅建议管理员使用。
- 缩短系统策略提醒，同时保留 Windows 10/11 现场验收状态、可还原测试机逐项验证、默认关闭、账户/环境预检、跳过不支持项目和失败电脑不应用策略等信息。

## 保留的关键信息

- “教师端已接收”不表示学生已收到或已读；状态仍由单条消息显示。
- 手机操作成功只表示电脑回执，未声称限制效果已经验证。
- 目标电脑范围、无自动到期的长期策略、预检失败的后果和真实 Windows 验收状态均未删减。

## 验证

- `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore`：通过，0 警告、0 错误。
- `dotnet build src/VeyonCampus.Companion/VeyonCampus.Companion.csproj --configuration Release --no-restore`：通过；有一条既有 Avalonia `AVLN3001` 窗口构造器警告。
- `dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj`：78 项检查全部通过。检查项目本身输出 5 条既有 CA1416 平台兼容性警告。
- `node --check` 检查 `app.js` 和 `service-worker.js` 通过；HTML 包含 87 个唯一 ID，静态 JavaScript 引用完整；本地页面和样式均返回 HTTP 200；`git diff --check` 通过。
- 本机 CUA 无可用浏览器实例，启动时报告 `Sky Computer Use native pipe startup failed`；因此未完成浏览器截图回放，窄屏视觉验收仍待浏览器/手机实机。
