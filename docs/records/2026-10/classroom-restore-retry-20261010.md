# 课堂待恢复项查看与一键重试

日期：2026-10-10

## 用户操作

- 下课时仍未确认解除的课堂策略会显示在 Teacher 桌面课堂页；已配对手机也会显示同一待恢复列表。
- 列表只显示原机房、稳定电脑编号和策略类别。教师无需选择 session、电脑或策略，点一次“重试待恢复项”即可处理所有可安全匹配的条目。
- 如果课堂仍在进行，手机端会禁用该动作并提示下课后重试。

## 恢复边界

- 状态只从本机课堂账本、课堂快照、校区/机房档案和当前 Veyon 目录读取；CloudBase 不参与。
- 重试按原 session 中的电脑编号匹配现有机房，要求校区/机房 ID、机房名称、电脑编号范围和当前目录目标唯一吻合。解析得到的现行目标地址只在内存中使用，不写入恢复账本、手机列表或本次审计目标。
- 对匹配设备重新读取 Agent 签名状态。只有当前策略仍与原课堂拥有 revision 一致时才发送解除命令，并再次签名读回确认。外部 revision、离线、缺失或重复机房、目录目标不匹配均不覆盖且保留待恢复项。
- 原签名校区从本堂课残留的签名上下文或本机签名密钥集合中唯一解析；无法确认时不发送策略。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：0 警告、0 错误。
- TeacherConsole Release 单独构建：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：**65/65** 通过，包含稳定标签匹配、机房/范围歧义拒绝、配对授权、待恢复列表脱敏，以及重试端点的平台限制检查；Windows 上会继续验证活动课堂冲突响应。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 与 `git diff --check`：通过。

## 尚未完成

当前环境不是 Windows，也没有真实学生电脑或手机，因此未实测当前 Veyon 目录导出、Agent 身份读回、浏览器/AppLocker 实际解除、Teacher 桌面交互或手机证书/LAN 流程。真实设备验收仍在[课堂模式子任务 8](../../../specs/classroom-modes/tasks.md)中。

需求与安全边界见[课堂模式需求](../../../specs/classroom-modes/requirements.md)和[课堂模式设计](../../../specs/classroom-modes/design.md)。
