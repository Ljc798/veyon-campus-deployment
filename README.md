# Veyon Campus Deployment

Veyon Campus 为学校提供 Windows 教师控制台和学生部署工具，支持 Veyon 校区配置、学生机网站限制、AppLocker 应用限制、长期系统策略及教师手机局域网控制。当前源码中的 TeacherConsole/StudentSetup 项目版本为 1.0.11，Worker 为 0.4.63、Student Companion 为 0.4.64、Student Agent 为 0.4.50、Core 为 0.4.48；这些源码版本不代表设备安装版本或实机验收结果。手机控制代码已加入按 Veyon 地点浏览、两列电脑名卡片、单机详情、签名状态预检与 Agent 原子版本冲突检查；手机策略写入需要部署带版本比较能力的新 Agent，详见[手机控制规格](specs/mobile-teacher-control/requirements.md)。2026-10-16 本机 macOS 使用 .NET SDK 10.0.401 完成完整可移植检查 **78/78**；TeacherConsole、StudentSetup 和 Agent Release 构建均为 **0 警告、0 错误**，手机 JavaScript 语法检查通过。Windows、Android/iOS 实机验收仍开放。发行包签名及更新可用状态以[发布执行清单](docs/发布执行清单.md)为准。

项目处于功能集成和验收阶段。限制与双端更新代码已经实现并通过可移植检查；浏览器/系统策略在受支持 Windows 设备上的实际效果、手机证书和 LAN 行为、更新安装与恢复仍需设备验收。稳定版 v1.0.10 已通过发布工作流发布到 GitHub、GitCode 和 CloudBase；TeacherConsole/StudentSetup 当前源码版本为 1.0.11，待本次标签流水线构建并发布。

- **项目现状和文档入口：**[文档导航与当前概览](docs/README.md)
- **稳定版发行与生产门槛：**[稳定版发布执行清单](docs/发布执行清单.md)
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

**安装程序请从 [GitHub Releases](https://github.com/Ljc798/veyon-campus-deployment/releases) 或 [GitCode Releases](https://gitcode.com/ljc_1126/veyon-campus-deployment/releases) 下载对应角色的 Setup EXE，并核对同一 Release 附带的 `SHA256SUMS`**。学生电脑使用 Student 版，教师电脑使用 Teacher 版。不要下载页面中的“Code → Download ZIP”或 “Source code (zip)”，它们是源代码，不是安装程序。当前稳定版提供 Teacher/Student EXE 和 SHA-256 清单；旧版 ZIP 说明仅供历史参考。

Beta 使用 `beta-vX.Y.Z` 标签；推送后会构建并冒烟测试 Teacher/Student EXE，再创建仅供手动下载的 GitHub 预发布并附 SHA-256 清单。正式发布只接受与项目版本一致的稳定 `vX.Y.Z` 标签；Windows installers 工作流会构建并发布两个角色 EXE 和 SHA256SUMS 到 GitHub、配置的 GitCode 镜像及 CloudBase 签名版本目录。应用内更新从 GitHub 下载 EXE。现有 `v0.4.56` 标签指向提交 `961242e`，不要移动或复用它。当前稳定版和待发布版本、凭据、签名、许可状态见[稳定版发布执行清单](docs/发布执行清单.md)。

## 在线站点

- **软件下载页：**[打开最新下载页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/download)

- **公开介绍页：**[CloudBase 体验站点](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/)
- **管理员工作区：**[打开登录页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin)。使用 CloudBase Auth 登录；网站没有默认管理员用户名或密码。
- **版本发布：**登录后 owner/admin 可在[版本发布页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin/releases)查看入口。服务端 GitHub 触发令牌未配置时，发布按钮保持禁用。

体验站点使用 CloudBase 默认域名；浏览器首次访问可能显示 CloudBase 访问提示页。

## 安全与发布边界

- v1.0.10 已通过稳定版发布工作流发布到 GitHub、GitCode 和 CloudBase；本仓库许可证与 Veyon 等第三方再分发事项仍需负责人确认。公开仓库并不等同于获得再分发授权。
- 学生机限制和更新依赖已安装并信任的教师/发布者密钥；v1.0.10 已发布签名版本元数据，真实设备上的更新安装与恢复仍需验收。
- 原 PowerShell v1.0 工具与当前 App 是两套独立工具。旧学生脚本会修改计算机名和 Veyon 配置，并可能覆盖运行脚本的本地管理员密码；失败时不会自动撤销已完成操作。旧脚本使用说明见[归档脚本指南](docs/archive/legacy/脚本使用指南.md)。
