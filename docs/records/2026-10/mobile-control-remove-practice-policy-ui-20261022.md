# 手机控制界面清理：移除课堂练习策略预设入口

后续完整停用旧课堂临时策略启用流程的版本见[1.1.23 记录](classroom-practice-mode-removal-20261022.md)。

日期：2026-10-22  
版本：TeacherConsole 1.1.16  
状态：本地 TeacherConsole 试点安装器已构建；桌面现场验收待用户执行。

## 调整内容

- “练习模式”是旧的课堂策略管控流程：课堂期间临时应用已保存的网站/应用策略，并在下课时恢复；不是答题、测验或普通课堂沟通。
- 项目不使用这类课堂练习。已从“手机控制”窗口移除“课堂练习策略”面板、电脑/账户范围预览、预设保存/覆盖/删除操作及其管理器绑定。
- 手机配对、连接、操作记录、课堂互动、课堂快捷管控和教师端“策略与预设”日常策略管理保留。
- 本轮没有删除教师本机已有预设、旧课堂策略执行代码或下课恢复记录，避免丢弃历史数据或干扰仍在执行的恢复流程。

## 构建

- 使用 .NET SDK 10.0.401，通过 `scripts/package-windows-offline.ps1` 构建 TeacherConsole 1.1.16 Classroom Quick-Control Pilot。
- Release 发布与 Inno Setup 安装器编译成功；TeacherConsole 有 0 个编译错误、2 条既有 `TextBox.Watermark` 过时警告。
- 安装器：`artifacts/test-build-phase7e-remove-practice-policy-ui-20261022/VeyonCampus-TeacherConsole-1.1.16-Remove-Practice-Policy-UI-Pilot-Test.exe`
- 大小：69,333,912 bytes。SHA-256：`60DE4B5BA3F1F6172984E702A35BE62C1C4420C041BA1EAB7409B7ED2B393CC8`。
- 载荷角色为 `TeacherConsole` 1.1.16；包含课堂快捷管控试点标记。没有发行 PEM 公钥，因此测试安装器中的应用内自更新保持关闭。
- 未运行测试套件、启动应用、安装、操作学生电脑或发布到 GitHub、GitCode、CloudBase 或下载站。

## 用户现场测试

1. 安装该 TeacherConsole 测试包并打开“手机配对与连接”，确认“课堂练习策略”不再显示。
2. 检查生成连接码、等待手机确认、已配对手机和操作记录仍正常显示。
3. 返回课堂控制页，确认课堂互动及快捷管控仍可见；进入“策略与预设”，确认网站、应用、系统策略管理仍可用。
4. 如果此前已启用旧练习模式或有未完成恢复项，先在可恢复测试电脑上查看策略状态并确认下课恢复，不要直接清除教师本地数据。
