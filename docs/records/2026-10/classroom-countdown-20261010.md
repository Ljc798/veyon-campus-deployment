# 课堂倒计时实现记录

日期：2026-10-10（Asia/Hong_Kong）

## 实现范围

Teacher 在当前活动课堂中可设置 1–180 分钟并启动倒计时，默认 15 分钟；桌面显示剩余时间，到时显示“时间到”。倒计时不暂停/结束课堂，也不改变网站、应用或系统策略。下课时按当前 session ID 清理计时。

计时数据只包含 schema、`SessionId`、UTC 开始时间和截止时间，独立保存在 `%LOCALAPPDATA%\VeyonCampus\Teacher\classroom-countdown.json`。Teacher 重启后读取与当前活动 session 匹配的截止时间；已配对手机通过现有 `/api/session` 只读获取截止时间，PWA 以系统时间更新显示。没有新增 CloudBase API，也没有学生目标或姓名进入计时文件。

## 主要实现

- `src/VeyonCampus.Core/ClassroomCountdown.cs`：范围受限的 UTC 倒计时模型、session 隔离、严格 JSON/重复字段拒绝、文件锁、原子保存和损坏文件保护。
- `src/VeyonCampus.App/TeacherViewModel.cs`、`TeacherWindow.axaml` 与 `TeacherWindow.axaml.cs`：现有活动课堂卡片中的单时长控件、剩余时间和结束动作；UI tick 只刷新显示，时间计算始终基于持久 UTC deadline。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`：当前 session 对应的可选 `activeClassroomCountdown.deadlineUtc`。损坏计时文件只省略计时字段，不影响会话或策略 API。
- `src/VeyonCampus.App/MobileWeb/`：PWA 只读计时显示，页面每秒按 deadline 更新；service worker 缓存升至 v7 以刷新新界面。
- `tests/VeyonCampus.Checks/ClassroomCountdownChecks.cs` 与 `MobileControlApiChecks.cs`：边界、剩余时间、重启读回、session 隔离、损坏文件保护、API 回传与损坏状态降级。
- [课堂倒计时使用指南](../../课堂倒计时使用指南.md) 与 [专项规格](../../../specs/classroom-countdown/requirements.md)。

## 自动验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1 -p:VeyonCampusIncludeInstaller=true`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -m:1 -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=true`：成功，0 警告、0 错误。
- `.NET` 检查集：**69/69 通过**，包含计时边界、存储、API 正常回传及损坏文件降级。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`、`node --check src/VeyonCampus.App/MobileWeb/service-worker.js` 与 `git diff --check`：通过。

## 实机验收边界

尚未在 Windows 上验证教师界面交互、睡眠/重启后的桌面计时恢复、下课文件清理，也未在真实手机和校园 LAN 验证 PWA 倒计时同步。时钟手动前后调整会按系统 UTC 时钟影响显示；本功能不是系统级单调计时器。实机测试前不将本功能标记为现场已验收。
