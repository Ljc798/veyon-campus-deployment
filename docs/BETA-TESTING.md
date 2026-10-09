# Veyon Campus Beta 测试指南

Beta 安装包适用于 Windows 10/11 x64 的手动安装测试。GitHub Beta Release 附有 Teacher、Student 安装包和 `SHA256SUMS`；它不会发布到 Gitee 或 CloudBase 自动更新目录。

## 下载与校验

1. 从 GitHub Releases 打开标记为 **Pre-release** 的 `Veyon Campus 0.4.58 Beta`。
2. 按测试目标下载 Teacher 或 Student 安装包，以及 `SHA256SUMS`。
3. 在 PowerShell 中运行 `Get-FileHash .\VeyonCampus-*-Setup-0.4.58-win-x64.exe -Algorithm SHA256`，将输出与 `SHA256SUMS` 中对应文件名的哈希值比对。
4. 核对一致后再运行安装程序。Beta 安装包未进行 Authenticode 签名，Windows 可能显示未知发布者提示。

## 测试建议

- 使用可恢复的测试电脑、虚拟机快照或系统备份；不要先在正式课堂设备或真实学生账户上测试。
- Teacher 与 Student 安装包分别安装在对应角色的 Windows 电脑上。首次仅验证启动、只读检查、卸载和重启后状态，再逐项测试策略、局域网控制及更新。
- 记录 Windows 版本/版本号、电脑角色、操作步骤、预期结果、实际结果和错误信息。提交前遮盖账号、校园信息、IP 地址、令牌和日志中的个人信息。
- Beta 重点验收清单见[测试验收与发布清单](测试验收与发布清单.md)；当前未完成的 Windows 实机项目仍应标为待验收。
