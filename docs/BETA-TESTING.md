# Veyon Campus Beta 测试指南

本指南适用于 GitHub Releases 中实际列出的 Beta 预发布，不适用于 GitHub Actions 临时构建产物。Beta 安装包仅供 Windows 10/11 x64 手动测试；它不会发布到 Gitee 或 CloudBase 自动更新目录。

**2026-10-12 核对：**GitHub Releases 当前有 `beta-v1.0.1`（Veyon Campus 1.0.1 Beta）和 `beta-v0.4.58` 两个预发布，各自附 Teacher/Student 安装包与 `SHA256SUMS`；`beta-v1.0.2` 目前只有标签，没有 Release 条目。现有安装包未进行 Authenticode 签名，均不是稳定版。`1.0.3` 稳定候选尚未打 tag 或发布。

## 下载与校验

1. 从[GitHub Releases](https://github.com/Ljc798/veyon-campus-deployment/releases)选择需要测试的 **Pre-release**，确认标签和说明中的版本号。
2. 按测试目标下载 Teacher 或 Student 安装包，以及同一发行条目中的 `SHA256SUMS`。
3. 在 PowerShell 中运行 `Get-FileHash .\VeyonCampus-*-Setup-*-win-x64.exe -Algorithm SHA256`，逐个对照 `SHA256SUMS` 中相同文件名的摘要。
4. 仅在文件名、角色和摘要均匹配时运行安装程序。Beta 安装包未进行 Authenticode 签名，Windows 可能显示未知发布者提示。

## 测试建议

- 使用可恢复的测试电脑、虚拟机快照或系统备份；不要先在正式课堂设备或真实学生账户上测试。
- Teacher 与 Student 安装包分别安装在对应角色的 Windows 电脑上。先验证启动、只读检查、卸载和重启后状态，再逐项测试策略、局域网控制及更新。
- 记录 Windows 版本、电脑角色、操作步骤、预期结果、实际结果和错误信息。提交前遮盖账号、校园信息、IP 地址、令牌和日志中的个人信息。
- Beta 重点验收清单见[测试验收与发布清单](测试验收与发布清单.md)；未完成的 Windows 实机项目仍应标为待验收。
