# Veyon Campus Deployment

Veyon Campus 为学校提供 Windows 教师控制台和学生部署工具，支持 Veyon 校区配置、学生机网站限制、AppLocker 应用限制、长期系统策略及教师手机局域网控制。当前源码候选版本为 Teacher/Student/Worker/Companion 0.4.59、Student Agent 0.4.44；课堂通知可附一个网址，由学生主动点击后通过默认浏览器打开。课堂状态和共享链接的 Windows 双机、手机现场验收仍待完成。此前 Beta 版本和测试包记录见[项目文档](docs/README.md)。

项目处于功能集成和验收阶段。限制与双端更新代码已经实现并通过可移植检查；浏览器/系统策略在受支持 Windows 设备上的实际效果、手机证书和 LAN 行为、更新安装与恢复仍需设备验收。当前还没有正式签名应用发行版；Actions 构建产物用于临时测试，不能代表正式在线更新已经可用。

- **项目现状和文档入口：**[文档导航与当前概览](docs/README.md)
- **任务状态及验收证据：**[开发路线与任务清单](docs/开发路线与任务清单.md)
- **架构与组件边界：**[架构与实施边界](docs/veyon_architecture_summary.md)
- **开发、构建、打包：**[开发与部署指南](docs/开发与部署指南.md)
- **Beta 安装与测试：**[Beta 测试指南](docs/BETA-TESTING.md)
- **课堂共享链接：**[使用指南](docs/课堂共享链接使用指南.md)
- **课堂任务进度：**[使用指南](docs/课堂任务进度使用指南.md)
- **网站前端：**[website README](website/README.md)
- **参与贡献：**[CONTRIBUTING](CONTRIBUTING.md)

## 仓库结构

- `src/`：Teacher、Student、Worker、Agent 和共享 Core 源码。
- `website/`、`cloudfunctions/`、`cloudbase/`：管理网站、CloudBase API 与数据库迁移。
- `scripts/`：构建、发布、部署和验收自动化；`scripts/legacy/` 集中存放旧 PowerShell 脚本及其配套教程视频。
- `packaging/`、`installer/`：第三方再分发材料、固定版 Veyon 安装器及 Windows 安装器定义。
- `docs/`、`specs/`、`tests/`：操作文档、需求规格和自动检查。

## Tag、测试产物与 Release

Tag 只指向一个源代码提交，因此 GitHub 为 Tag 提供的 ZIP/TAR 下载仍是源代码。Release 是附在 Tag 上的发行记录，可以包含安装包等二进制附件；GitHub 的 Release 页面会同时提供这些附件和对应源码归档。[GitHub 关于 Release 的说明](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

Beta 使用 `beta-vX.Y.Z` 标签；推送后会构建并冒烟测试 Teacher/Student 安装包，再创建仅供手动下载的 GitHub 预发布，附安装包和 SHA-256 清单。Beta 不会推送 Gitee，也不会向 CloudBase 发布自动更新版本。正式发布仍只接受与项目版本一致的稳定 `vX.Y.Z` 标签，并会发布到 GitHub/Gitee 和 CloudBase。现有 `v0.4.56` 标签指向提交 `961242e`，不要移动或复用它；Beta 验收通过后，可在相同代码上建立匹配的稳定标签；若需修复代码，应递增项目版本后重新构建。

## 在线站点

- **公开介绍页：**[CloudBase 体验站点](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/)
- **管理员工作区：**[打开登录页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin)。使用 CloudBase Auth 登录；网站没有默认管理员用户名或密码。
- **版本发布：**登录后 owner/admin 可在[版本发布页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin/releases)查看入口。服务端 GitHub 触发令牌未配置时，发布按钮保持禁用。

体验站点使用 CloudBase 默认域名；浏览器首次访问可能显示 CloudBase 访问提示页。

## 安全与发布边界

- 正式发布已确定面向公开 GitHub/Gitee；项目许可证及第三方再分发审查、签名密钥和发行凭据尚未完成。当前仓库可公开浏览不代表已授予代码再分发权限。
- 学生机限制和更新依赖已安装并信任的教师/发布者密钥。没有生产签名发行前，更新器会失败关闭。
- 原 PowerShell v1.0 工具与当前 App 是两套独立工具。旧学生脚本会修改计算机名和 Veyon 配置，并可能覆盖运行脚本的本地管理员密码；失败时不会自动撤销已完成操作。旧脚本使用说明见[归档脚本指南](docs/archive/legacy/脚本使用指南.md)。
