# 低选择课堂模式实现记录

日期：2026-10-10

## 用户操作

- 开始课堂固定为“正常课堂”，不再选择模式、目标电脑或策略预设。
- 桌面和手机课堂卡片都只有一个切换按钮：“开始练习”或“恢复正常”。
- 练习自动采用当前校区最近更新的网站和应用临时预设；长期系统策略不会进入课堂流程。
- 手机控制页在课堂进行时把单项策略与电脑范围调整折叠到“单独控制策略与电脑”，高级入口仍可展开。
- 操作结果显示自动采用的预设名称和逐台处理结果；学生 Companion 显示签名课堂模式。

## 实现边界

- `ClassroomModeStateStore` 将模式和逐台拥有 revision 保存在教师本机；以随机 session/target ID 关联，不保存网络地址、学生姓名、策略正文或密钥。
- 练习前读取签名 Agent 状态，只覆盖 Disabled 或本堂课仍拥有的策略版本；发送后再次读回 revision/mode，确认后才记录拥有权。未知、离线、外部修改和读回不匹配逐台报告并保留安全边界。
- AppLocker Enforce 继续执行 Audit → 展示统计 → 教师明确确认；票据绑定手机/桌面身份、本堂课和练习模式，执行前重新核验。
- 恢复正常和下课只解除仍与本堂课拥有 revision 匹配的网站/应用策略；长期系统基线不会被修改。桌面状态可滚动查看逐台结果。
- 课堂签名快照 v2 增加模式枚举，Agent 与 Companion 保留旧 v1 读取兼容。Companion 使用旧 loopback 状态路径时隐藏新字段，避免旧 Companion 严格 JSON 解析失败。
- 手机 API 只接受固定的 `normal/practice` 与可选审核票据；目标和预设由 Teacher 当前 session 与本校最新保存项决定。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：0 警告、0 错误。
- TeacherConsole Release build：0 警告、0 错误。
- StudentSetup Release build：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-restore`：**65/65** 通过；包含 v1/v2 状态兼容、模式状态持久化、手机 session 模式和“最新网站/应用、排除系统策略”的默认选择检查。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`：通过。
- `git diff --check`：通过。

## 尚未完成

- 尚未在 Windows 真实桌面或学生机验证浏览器阻断、AppLocker Audit/Enforce、策略恢复、Agent SYSTEM 通道与 Companion 展示；Android/iOS 手机、证书信任、真实校园 Wi-Fi/LAN 也未测试。
- 如果设备在下课时离线，拥有记录会保留在教师电脑；当前还没有结束课堂后的待恢复项目查看/重试界面。本堂课仍活动时可再次点击“恢复正常”重试。该入口作为下一项功能跟踪，不把持久化记录描述为已经可重试。
- 代码构建和签名回读不等于 Windows 策略已生效；真实效果仍须按验收步骤在可恢复测试机验证。

需求、设计与任务状态见[课堂模式规格](../../../specs/classroom-modes/requirements.md)、[设计](../../../specs/classroom-modes/design.md)和[子任务](../../../specs/classroom-modes/tasks.md)。
