# Veyon Campus 1.0.2 Beta 测试

这是基础功能测试版，保留 Veyon 部署、学生配置包和网站黑名单/白名单。应用限制、长期系统限制和手机控制暂不提供；相关源代码仍留在仓库，供后续版本逐项启用。此 Beta 仅供 Windows 实机验收，不会发布到 Gitee 或 CloudBase 自动更新目录。

## 下载与校验

1. 在 GitHub Releases 打开 **Veyon Campus 1.0.2 Beta**，下载 Teacher 或 Student 安装包及 `SHA256SUMS`。
2. 在 PowerShell 中运行：

   ```powershell
   Get-FileHash .\VeyonCampus-Teacher-Setup-1.0.2-win-x64.exe -Algorithm SHA256
   Get-FileHash .\VeyonCampus-Student-Setup-1.0.2-win-x64.exe -Algorithm SHA256
   ```

3. 将输出的 SHA-256 与清单中对应文件的值比对，一致后再运行安装包。

安装包没有 Authenticode 签名，Windows 可能显示“已保护你的电脑”。这是未签名测试版的系统提示。

从 1.0.1 Beta 升级前，如果学生电脑曾启用应用限制或系统限制，请先用 1.0.1 Teacher 端解除，并确认学生电脑已恢复。1.0.2 基础版不含这些管理入口；旧限制状态不会因隐藏入口或更换安装包而自动清除。

## 测试范围

- 使用可恢复的 Windows 10/11 x64 测试电脑；Teacher 和 Student 安装包分别安装在对应角色的电脑上。
- 验证安装、启动、卸载、Veyon 一键配置、网站黑名单/白名单，以及重复运行学生配置时 Agent 是否正确更新。
- 在 Edge、Chrome 和 Firefox 中检查网站限制是否按预期刷新。
- 记录 Windows 版本、操作步骤、预期结果、实际结果和错误信息。提交前遮盖校园信息、账号、IP 地址及日志中的个人信息。
- 本机自动检查通过不等于 Windows 实机验收；未实测的行为仍标为待验收。
