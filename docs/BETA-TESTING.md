# Veyon Campus 1.0.0 Beta 测试

本测试版以已发布的 `v0.4.56` 基础功能为起点，并包含学生 Agent 重配修复。它不合并后续 `develop` 上的课堂和二维码配对等新功能。Beta 仅供 Windows 实机验收，不会发布到 Gitee 或 CloudBase 自动更新目录。

## 下载与校验

1. 在 GitHub Releases 打开 **Veyon Campus 1.0.1 Beta**，下载 Teacher 或 Student 安装包及 `SHA256SUMS`。
2. 在 PowerShell 中运行：

   ```powershell
   Get-FileHash .\VeyonCampus-Teacher-Setup-1.0.1-win-x64.exe -Algorithm SHA256
   Get-FileHash .\VeyonCampus-Student-Setup-1.0.1-win-x64.exe -Algorithm SHA256
   ```

3. 把输出的 SHA-256 与清单中对应文件的值比对，一致后再运行安装包。

安装包没有 Authenticode 签名，Windows 可能显示“已保护你的电脑”。这属于测试版的未签名提示。

## 测试范围

- 使用可恢复的 Windows 10/11 x64 测试电脑；Teacher 和 Student 安装包分别安装在对应角色的电脑上。
- 先验证安装、启动和卸载，再按需测试 Veyon 部署、学生网站策略、应用/系统限制及 Agent 重复配置。
- 每项都记录 Windows 版本、操作步骤、预期结果、实际结果和错误信息。提交前遮盖校园信息、账号、IP 地址及日志中的个人信息。
- 代码通过本机自动检查不等于 Windows 实机验收；未测试的行为继续标为待验收。
