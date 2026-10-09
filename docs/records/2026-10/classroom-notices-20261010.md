# 全班通知实现记录

日期：2026-10-10

范围：P1 双向课堂事件通道的教师通知能力。代码提交后以项目任务主表和专项规格为当前状态依据。

## 用户操作

Teacher 桌面和已配对手机的课堂互动区各只有一个通知输入框和一个发送动作。教师不需要逐台选择设备或选择网络；系统自动将通知绑定到当前活动课堂。发送反馈说明提交目标数量，不宣称学生已读。

## 数据与权限

- 通知最多 500 字符，教师校区密钥签名并绑定当前 session，最多存活两分钟；连续发送至少间隔五秒。
- Teacher 内存队列只存一条目标为 `*` 的 class-wide `ClassroomNotice`。各学生 Agent 仍须用当前 session 的逐台授权读取，不把广播目标当成身份凭据。
- Companion 收到通知后与当前求助状态并列显示，不覆盖尚待学生确认的教师回复。
- 不保存云端或聊天历史；下课、服务结束或事件过期后不再投递。

## 代码与文档

- Core 事件协议和有界缓冲区支持单条全班事件及目标专属读取。
- Teacher API 增加 `POST /api/classroom/events/notice`；桌面与手机共享同一服务入口。
- 更新 Teacher 桌面、MobileWeb 和 Student Companion 展示，并补充签名、目标隔离、速率限制、下课和状态组合检查。
- 补充课堂事件规格、手机使用指南、架构摘要和文档索引。

## 验证

- `dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：成功，0 警告、0 错误。
- `dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build`：65/65 通过。
- `node --check src/VeyonCampus.App/MobileWeb/app.js` 与 `git diff --check`：通过。
- Chrome 390×844 浏览器回放：配对后进入已连接页面，发送一次通知；确认请求正文、输入清空、24 个当前课堂目标的提交反馈和通知卡片回显；页面无 JavaScript 异常或控制台错误。此回放用 mock API，不替代 Teacher/Agent 的签名链路检查；签名、API 授权和逐台读取由 .NET 自动检查覆盖。

Windows 双机和真实 Android/iOS 浏览器证书信任、校园 LAN、防火墙/VLAN、断线恢复与并发仍需现场验收。
