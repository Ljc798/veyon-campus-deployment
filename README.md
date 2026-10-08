# Veyon Campus Deployment

Veyon Campus 为学校提供 Windows 教师控制台和学生部署工具，支持 Veyon 校区配置、学生机网站限制、AppLocker 应用限制、长期系统策略及教师手机局域网控制。源码当前版本为 0.4.53；Student Agent 为 0.4.41。

项目处于功能集成和验收阶段。限制与双端更新代码已经实现并通过可移植检查；浏览器/系统策略在受支持 Windows 设备上的实际效果、手机证书和 LAN 行为、更新安装与恢复仍需设备验收。当前没有生产 Developer Release 密钥或签名应用发行版，因此客户端在线更新尚未启用。

- **项目现状和文档入口：**[文档导航与当前概览](docs/README.md)
- **任务状态及验收证据：**[开发路线与任务清单](docs/开发路线与任务清单.md)
- **架构与组件边界：**[架构与实施边界](docs/veyon_architecture_summary.md)
- **开发、构建、打包：**[开发与部署指南](docs/开发与部署指南.md)
- **网站前端：**[website README](website/README.md)
- **参与贡献：**[CONTRIBUTING](CONTRIBUTING.md)

## 在线站点

- **公开介绍页：**[CloudBase 体验站点](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/)
- **管理员工作区：**[打开登录页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin)。使用 CloudBase Auth 登录；网站没有默认管理员用户名或密码。
- **版本发布：**登录后 owner/admin 可在[版本发布页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin/releases)查看入口。服务端 GitHub 触发令牌未配置时，发布按钮保持禁用。

体验站点使用 CloudBase 默认域名；浏览器首次访问可能显示 CloudBase 访问提示页。

## 安全与发布边界

- 正式发布已确定面向公开 GitHub/Gitee；项目许可证及第三方再分发审查、签名密钥和发行凭据尚未完成。当前仓库可公开浏览不代表已授予代码再分发权限。
- 学生机限制和更新依赖已安装并信任的教师/发布者密钥。没有生产签名发行前，更新器会失败关闭。
- 原 PowerShell v1.0 工具与当前 App 是两套独立工具。旧学生脚本会修改计算机名和 Veyon 配置，并可能覆盖运行脚本的本地管理员密码；失败时不会自动撤销已完成操作。旧脚本使用说明见[归档脚本指南](docs/archive/legacy/脚本使用指南.md)。
