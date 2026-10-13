# Veyon Campus 1.0.3 Beta 实机测试

`beta-v1.0.3` 是供 Windows 10/11 x64 手动验收的基础功能预发布版。请从 [GitHub Release](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/beta-v1.0.3) 或 [Gitee Release](https://gitee.com/jack7981/veyon-campus-deployment/releases/tag/beta-v1.0.3) 下载。实机测试仍未完成；自动构建和安装卸载检查不代表课堂环境验收通过。

本版包含 Veyon 部署、学生配置包及网站黑名单/白名单管理；应用限制、长期系统限制和手机控制暂不开放，也不会发布到 CloudBase 自动更新目录。如果学生机启用了旧 Beta 的应用或系统限制，升级前请先在原 Teacher 端解除，并确认学生机恢复正常。安装新版本不会自动清除旧系统策略。

## 下载和校验

### GitHub

每台电脑只下载一个完整 `.exe`：Teacher 安装包用于教师电脑，Student 安装包用于学生电脑。Release 页面提供 `SHA256SUMS` 供下载后校验。

### Gitee

Gitee Release 为 Teacher 和 Student 各提供一个完整安装包的直链，以及校验文件链接。由于 Gitee 附件大小限制，文件托管在 GitHub Release；点击所需角色的链接即可下载完整 EXE，无需合并分卷。

下载完成后，也可以手动检查摘要：

```powershell
Get-FileHash .\VeyonCampus-Student-Setup-1.0.3-win-x64.exe -Algorithm SHA256
Get-FileHash .\VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe -Algorithm SHA256
```

摘要必须与 `SHA256SUMS` 中同名文件的值一致后才能运行。安装包没有 Authenticode 签名，Windows 可能显示“已保护你的电脑”或未知发布者提示。

## 实机测试范围

- 使用可恢复的 Windows 10/11 x64 测试电脑；Teacher 与 Student 安装包分别安装在对应角色电脑上。
- 检查安装、启动、卸载、Veyon 配置、学生配置包导入，以及网站策略在 Edge、Chrome 和 Firefox 中的应用效果。
- 重复执行学生配置并检查已安装 Agent 的更新/修复行为。
- 记录 Windows 版本、电脑角色、操作步骤、预期结果、实际结果和错误信息。提交前遮盖校园名称、账号、IP 地址和日志中的个人信息。

Beta 安装包未进行 Authenticode 签名。首次测试应在可恢复的测试电脑上进行，不要直接用于正式课堂设备或真实学生账户。实机验收结果单独记录；未完成的项目继续标记为待验收。
