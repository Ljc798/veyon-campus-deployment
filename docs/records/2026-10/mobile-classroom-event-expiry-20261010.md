# 手机课堂事件到期清理记录

日期：2026-10-10（Asia/Hong_Kong）

## 完成内容

教师手机页之前会把已读取的求助与通知留在当前课堂内存列表中，直到下课或重新连接；求助过期后，页面仍会显示回复输入框，但服务端已拒绝过期求助的回复。现在页面按事件签名中的有效期自动清理：未回复的求助和全班通知到期后消失；已回复求助状态会保留到关联事件中最晚的有效期，再自动收起。单条教师回复正文在自身到期时清空，不会跟着较晚的解决状态继续显示。

手机页验证收到事件的时间范围，并用单个到期计时器清理当前课堂卡片。切换 session 或退出时会取消计时器并清除内存内容。没有新增设置、按钮或历史清理步骤。

## 实现位置

- `src/VeyonCampus.App/MobileWeb/app.js`：限制未来签发时间；忽略响应期间刚过期的事件；按关联事件有效期管理求助卡片，并自动清除通知与过期求助。
- `specs/classroom-event-channel/requirements.md`、`design.md`、`tasks.md`：记录事件缓存与页面到期行为。

## 验证

- `node --check src/VeyonCampus.App/MobileWeb/app.js`：通过。
- 临时 Node VM UI smoke：确认通知到期后消失、已回复求助保持至关联解决状态到期、回复正文按自身有效期隐藏，最后显示精简空状态；通过。此 smoke 使用模拟 DOM 和短时效事件。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release -p:VeyonCampusIncludeInstaller=true`：完整 **66/66** 检查通过。
- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -p:VeyonCampusIncludeInstaller=true -m:1`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=true`：TeacherConsole 构建成功，0 警告、0 错误。
- `git diff --check`：通过。

## 验收边界

上述检查覆盖网页脚本逻辑、模拟事件到期和跨平台构建，不证明真实手机浏览器后台计时精度、教师/学生 Windows 双机或校园 LAN 实机行为。实机验收仍按[双向课堂事件通道任务](../../../specs/classroom-event-channel/tasks.md)跟踪。
