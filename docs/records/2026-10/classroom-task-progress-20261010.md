# 课堂任务进度实现记录

日期：2026-10-10（Asia/Hong_Kong）

代码状态：本功能已单独本地提交，尚未推送。

## 实现范围

教师在活动课堂卡片中添加单行任务、标记完成/重开或删除。教师重启后按本机活动课堂恢复；下课按 SessionId 清除。已配对手机复用现有认证的 `/api/session` 只读展示任务和完成数。任务不发送给学生电脑，不写入 CloudBase，也不采集学生完成情况。

界面保持单一任务类型、单一输入行和清单；每堂课最多 20 项、每项最多 120 字，不提供优先级、截止时间、分组、目标设备或手机编辑选项。

## 主要实现

- `src/VeyonCampus.Core/ClassroomTaskProgress.cs`：SessionId 绑定的有界数据模型和本机存储；校验标题/字段/容量，使用互斥锁、同目录临时文件和原子替换，损坏数据保留。
- `src/VeyonCampus.App/TeacherViewModel.cs`、`TeacherWindow.axaml` 与 `TeacherWindow.axaml.cs`：活动课堂输入、完成切换、移除、进度摘要和重启恢复；下课清除当前课堂清单。
- `src/VeyonCampus.App/TeacherMobileControlServer.cs`：只在已配对认证的当前课堂 session 返回有界进度；读取失败时省略可选字段，不阻断其它课堂状态。
- `src/VeyonCampus.App/MobileWeb/`：课堂模式卡片增加只读清单，以 `textContent` 显示标题并校验 API 数据；service worker 缓存升至 v9。
- `tests/VeyonCampus.Checks/ClassroomTaskProgressChecks.cs` 与 `MobileControlApiChecks.cs`：覆盖标题和数量边界、跨实例读回、session 隔离、增删/完成、损坏数据不覆盖、API 回传与损坏状态降级。
- [课堂任务进度使用指南](../../课堂任务进度使用指南.md) 与[专项规格](../../../specs/classroom-task-progress/requirements.md)。

## 自动验证

- `dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：成功，0 警告、0 错误。
- `dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -m:1`：成功，0 警告、0 错误。
- 完整 .NET 检查集：**71/71 通过**。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`、`node --check src/VeyonCampus.App/MobileWeb/service-worker.js` 与 `git diff --check`：通过。

## 实机验收边界

尚未在 Windows 桌面验证任务输入、完成切换、教师重启恢复和下课清理，也未在真实手机与校园 LAN 验证只读清单刷新。自动检查与跨平台构建不代表现场验收完成。
