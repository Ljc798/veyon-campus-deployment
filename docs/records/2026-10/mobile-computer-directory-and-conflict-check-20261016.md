# 手机电脑目录与策略冲突保护核查记录

日期：2026-10-16（Asia/Hong_Kong）

## 本次完善

- 手机按 Veyon 地点浏览电脑，卡片每行两张并保留空白图标位置；电脑名来自 Veyon 显示名映射，卡片、详情、确认和结果不回退展示 IP。没有有效电脑名时显示“未命名电脑”并禁用操作。
- 单机详情始终只选择一个 Veyon 目标，读取该电脑签名状态。刷新目录后按地点名称恢复选中地点；当前电脑被移除、重复映射或失去电脑名时，退出详情并停用该目标操作。
- 多教师更新同类策略时，以 Agent 报告的策略版本执行条件写入。Teacher 先验证当前状态并将逐台预期版本放入策略签名；Agent 在共享事务锁内比较版本。课堂练习策略的签发和本堂课拥有策略的恢复路径也接入这一比较，Agent 低于 0.4.50 时保留策略且明确提示更新。
- 增补逐类策略回归检查：版本一致时网站、应用和长期系统策略可验签；状态版本变化时拒绝；应用和系统运行时拒绝后保持已安装状态不变；Veyon 目标在地点间重复时准确标出冲突项。
- 服务端对长期系统策略额外检查 `Pending` 状态；目标仍有待复核事务或未报告版本时，不接受启用/解除请求。
- 修正两项检查与当前实现脱节的问题：教师工作流检查使用现有的 `WebsiteBlockDomains` 接口；浏览器策略回执检查按当前“优雅重启请求/不会强制关闭浏览器”的文案核验。修复手机测验无活动课堂返回 JSON null 时的可空注释警告。

## 自动验证

- `.NET SDK 10.0.401`，macOS 开发环境：`dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release`，完整可移植检查 **78/78 通过**。
- TeacherConsole Release 构建、StudentSetup Release 构建、Agent Release 构建：均 **0 警告、0 错误**。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 和 `node --check src/VeyonCampus.App/MobileWeb/service-worker.js`：通过。
- `git -c core.whitespace=cr-at-eol diff --check`：通过；保留原有 CRLF 文件行尾。

## 尚未完成的现场验收

本轮在 macOS 上完成 .NET 代码构建和可移植检查，没有声称 Windows 安装、Veyon/LAN 通信、真实 390px 手机页面或 Android/iOS 证书信任通过。还需在两位教师并发操作的 Windows 测试环境验证第二个旧版本命令由 Agent 拒绝，以及学生机状态与策略效果。跟踪项见[手机控制任务 10 和 15](../../../specs/mobile-teacher-control/tasks.md)。
