# 学生机改名待重启提示记录

日期：2026-10-10（Asia/Hong_Kong）

## 完成内容

学生电脑改名并读回待生效名称后，StudentSetup 的部署结果摘要现在会明确提示：保存工作、手动重启电脑，并在重启后重新打开 StudentSetup 核对名称。应用不自动重启电脑，也不增加“立即重启”按钮，维护人员可按学校安排选择重启时间。

提示只根据本次执行中的 `rename` 步骤和 `requires-reboot` 状态显示。历史运行摘要仍要求重新检查本机状态，不会被当作当前重启命令。

## 实现位置

- `src/VeyonCampus.App/MainViewModel.cs`：从当前执行步骤读出待生效改名状态，并将操作说明放在结果页已有摘要区域。
- `tests/VeyonCampus.Checks/Program.cs`：验证当前待重启结果显示保存/重启/复核指引，历史记录不显示当前重启提示。
- `docs/学生端首次部署与恢复指南.md`：同步结果页提示和手动重启边界。

## 验证

- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release -p:VeyonCampusIncludeInstaller=true`：完整 **67/67** 检查通过。
- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -p:VeyonCampusIncludeInstaller=true -m:1`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=true`：TeacherConsole 构建成功，0 警告、0 错误。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 与 `git diff --check`：通过。

## 验收边界

本轮验证 UI 状态映射和构建，没有在 Windows 上实际改名或重启。P5 的真实名称生效、重启后复核和设备恢复验收仍开放；本改动不代表 P5 阶段全部完成。
