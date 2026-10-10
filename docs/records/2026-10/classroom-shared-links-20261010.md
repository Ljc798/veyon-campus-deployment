# 课堂共享链接实现记录

日期：2026-10-10

规格：`specs/classroom-shared-links/`，对应路线图 P2“共享链接快捷入口”。

开发基线：`0b3a6fa`（课堂倒计时实现之后；本地分支未 push）。

## 行为

- 教师使用现有全班通知输入框，不新增链接配置、范围选择或接收目标。
- 通知仅含一个有效 HTTP(S) URL 时，学生 Companion 显示“打开课堂网址”。学生点击后使用系统默认浏览器；不自动启动浏览器。
- 配对手机的通知卡片将同一有效 URL 显示为安全的新标签链接。文本仍通过 DOM 文本节点呈现。
- 多个链接、无效链接、userinfo、其他协议或超长地址均无快捷入口。
- 消息、链接和打开失败状态共用通知原有两分钟有效期；通知过期、课堂结束或 session 变化时清除。托盘窗口由到期计时器更新。

## 实现范围

- Core 新增有界 HTTP(S) 链接解析器；未改变签名事件协议、CloudBase 数据或 Agent 授权。
- Companion 在点击时再次校验有效期，再通过系统 shell 请求默认浏览器打开。
- MobileWeb 通知卡片安全渲染单个 URL，并更新 Service Worker cache 名称以使浏览器加载新资源。
- 新增链接解析、通知到期和 session 隔离的可移植检查，以及专项规格和使用指南。

## 检查与边界

验证通过：

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -m:1`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：70/70 通过。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`、`node --check src/VeyonCampus.App/MobileWeb/service-worker.js` 和 `git diff --check`：通过。

自动检查不替代 Windows 默认浏览器点击、托盘到期、Android/iOS 浏览器和校园 LAN 现场验收。

本轮不 push；用户在 Windows 上测试已下载版本时，本地开发提交不会改变该安装包或 GitHub 上的构建。
