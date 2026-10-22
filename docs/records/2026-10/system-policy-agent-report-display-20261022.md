# 系统策略明确区分 Agent 回执与现场效果

日期：2026-10-22  
版本：TeacherConsole 1.1.27  
状态：本地试点安装包已构建；Windows、浏览器和手机现场验收留待用户统一测试。

## 调整

- 设备健康和系统策略摘要将签名回执描述为“Agent 已报告”，并注明实际 Windows 效果仍待现场核验。
- 教师桌面系统策略推送结果使用“Agent 回执/已读回策略”，逐台结果不再把 Agent 回执写成限制“已生效”。成功、部分、待核对和失败计数保持原行为。
- 手机设备状态和系统策略明细作相同区分。手机逐台操作原有“电脑已回应，实际限制效果仍需在学生电脑上确认”提示继续保留。
- 本次只调整状态展示语义和帮助文字，没有修改系统策略、AppLocker、Agent 协议、写入或恢复行为。

## 构建

- 使用 `scripts/package-windows-offline.ps1 -Role TeacherConsole -OutputDirectory artifacts/test-build-system-policy-agent-report-20261022 -ClassroomQuickControlManualTest -Clean` 构建。
- Windows x64 Release 安装器编译成功，0 个错误；2 条既有 `TextBox.Watermark` 过时 API 警告。Veyon 内嵌资源摘要和无 Developer Release 公钥时更新助手安全停用检查通过。
- 安装器：[TeacherConsole 1.1.27](../../../artifacts/VeyonCampus-Teacher-Setup-1.1.27-win-x64.exe)，69,335,938 bytes；SHA-256 `8DB7E401524D4DC5F820AD83F2C411CAA86B99EA0E006569AC6BDFE7D8627983`。
- 包含 `CLASSROOM-QUICK-CONTROL-PILOT.txt`。缺少 Developer Release PEM 公钥，应用内更新安全停用；安装器仅供可恢复测试设备使用。
- 没有运行测试套件、安装或启动应用，也没有进行 Windows、浏览器或手机现场验收；未发布到远端。

## 统一现场核对

1. 教师端应用系统限制后，检查总结果与每台结果使用 Agent 回执措辞，并明确 Windows 实际效果待核验。
2. 设备健康页及手机状态页中的系统策略显示“Agent 已报告/已读回”；pending、未知、冲突仍应保持独立状态。
3. 在可恢复测试机分别检查本机/有效 AppLocker 与注册表策略读回、教育软件启动、学生可写目录 EXE、网络和账户设置，再将真实观察结果与 Agent 回执分开记录。
