# Veyon Campus Deployment

帮助学校完成 Veyon 首次部署、校区配置和 Edge/Chrome 网站策略管理。桌面程序使用 C# + Avalonia，教师控制台与学生部署工具分别交付。

**当前为实验阶段。** 源码版本与验证证据统一查看[任务与当前状态](docs/开发路线与任务清单.md)。后续方向为 Student 四步 Wizard、正式安装器、后台 Agent 和受签名保护的更新流程；目标方案不代表已交付。

- **开始阅读：**[文档索引](docs/README.md)
- **开发与运行：**[开发部署指南](docs/开发与部署指南.md)
- **后续工作：**[架构与实施计划](docs/veyon_architecture_summary.md)
- **参与贡献：**[CONTRIBUTING](CONTRIBUTING.md)
- **网站开发：**[website](website/README.md)

## 在线站点

- **公开介绍页：**[CloudBase 体验站点](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/)
- **管理员工作区：**[打开登录页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin)。使用 CloudBase Auth 登录；网站没有默认管理员用户名或密码。
- **版本发布：**登录后 owner/admin 可在[版本发布页](https://veyon-control-d3gs8hmuyd09c00a7-1348081197.tcloudbaseapp.com/admin/releases)查看入口。服务端 GitHub 触发令牌尚未配置时，发布按钮保持禁用。

体验站点使用 CloudBase 默认域名；浏览器首次访问可能显示 CloudBase 的访问提示页。

## 原 PowerShell 脚本

原脚本 v1.0 与 App 是两套工具。脚本及视频保留原位置，使用说明移至[脚本使用指南](docs/archive/legacy/脚本使用指南.md)和[视频教程](视频教程/README.md)。

**旧学生脚本会修改计算机名、Veyon 配置并覆盖运行脚本的本地管理员密码，中途失败不会自动撤销已完成操作。** 不要把 App 的独立操作与权限保护说明套用到旧脚本。
