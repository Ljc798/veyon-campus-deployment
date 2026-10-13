# Veyon Campus 1.0.3 Beta 实机测试

`beta-v1.0.3` 是供 Windows 10/11 x64 手动验收的基础功能预发布版。实机测试仍未完成；自动构建和安装卸载检查不代表课堂环境验收通过。国内镜像首次发布完成后，从 GitCode/AtomGit 仓库的 Releases 页面下载；也可以使用 [GitHub Release 备用页](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/beta-v1.0.3)。

本版包含 Veyon 部署、学生配置包及网站黑名单/白名单管理；应用限制、长期系统限制和手机控制暂不开放，也不会发布到 CloudBase 自动更新目录。如果学生机启用了旧 Beta 的应用或系统限制，升级前请先在原 Teacher 端解除，并确认学生机恢复正常。安装新版本不会自动清除旧系统策略。

## 下载和校验

### 推荐方式：GitCode/AtomGit ZIP

进入 GitCode/AtomGit 仓库的 Releases 页面后，按电脑角色只选一个 ZIP。解压后先阅读“安装前请阅读.md”，然后运行对应角色的 EXE。GitCode 附件中的 `SHA256SUMS-BUNDLES` 用于校验 ZIP。

如果 GitCode 页面尚无 `beta-v1.0.3` Release，说明国内镜像还没有完成首次上传和完整性校验。此时可以通过下方 GitHub 备用下载继续测试，或等管理员完成镜像验证后再开始。

### GitHub 备用下载

现有 `beta-v1.0.3` GitHub Release 保留 Teacher 和 Student 两个完整 `.exe`，没有修改已发布的 Release。教师电脑使用 Teacher 安装包，学生电脑使用 Student 安装包。下载后用 `SHA256SUMS` 校验；无需下载或合并分卷。

下载完成后，也可以手动检查摘要：

```powershell
Get-FileHash .\VeyonCampus-Student-Setup-1.0.3-win-x64.exe -Algorithm SHA256
Get-FileHash .\VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe -Algorithm SHA256
```

摘要必须与 `SHA256SUMS` 中同名文件的值一致后才能运行。安装包没有 Authenticode 签名，Windows 可能显示“已保护你的电脑”或未知发布者提示；如果遇到安全拦截，请暂停并联系测试负责人，不要关闭 Windows 安全防护。

## 实机测试范围

- 使用可恢复的 Windows 10/11 x64 测试电脑；Teacher 与 Student 安装包分别安装在对应角色电脑上。
- 检查安装、启动、卸载、Veyon 配置、学生配置包导入，以及网站策略在 Edge、Chrome 和 Firefox 中的应用效果。
- 重复执行学生配置并检查已安装 Agent 的更新/修复行为。
- 记录 Windows 版本、电脑角色、操作步骤、预期结果、实际结果和错误信息。提交前遮盖校园名称、账号、IP 地址和日志中的个人信息。

Beta 安装包未进行 Authenticode 签名。首次测试应在可恢复的测试电脑上进行，不要直接用于正式课堂设备或真实学生账户。实机验收结果单独记录；未完成的项目继续标记为待验收。
