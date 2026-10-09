# 双向课堂事件通道：协议基础记录

日期：2026-10-10（Asia/Hong_Kong）

## 本阶段交付

按产品路线图中 P1「双向实时消息通道」开始落地。为减少设置与环境差异，选择路线图允许的 HTTPS 长轮询方案作为首版传输方向，复用现有 Teacher `39176` HTTPS、Student Agent `39174` 签名通道和校区 LAN；不新增端口、云端中继或用户连接选项。

本次完成独立的 Core 协议基础，代码位于 `src/VeyonCampus.Core/ClassroomEvents.cs`：

- 定义教师签名的短期授权，固定校区、课堂 session、单台目标、私有 IPv4 Teacher URL、TLS 证书 SHA-256 指纹和 32 字节随机访问令牌；授权最长两分钟。
- 定义按发送者角色约束的课堂事件，包括求助、教师确认、教师回复、学生确认解决和课堂通知；严格校验 schema、目标、时间、正文长度、事件类型及关联 ID。
- 定义 Agent 侧内存授权状态，拒绝重复/旧授权，并在授权过期、校区或 session 改变、课堂结束时不返回可用授权。
- 定义有界内存事件缓冲，最多保留 4 个 session、每 session 512 个事件、单页 50 项；事件保留 15 分钟，支持按学生目标过滤和单调游标读取。
- 在 `ClassroomEventProtocolChecks` 中覆盖签名与公钥固定、篡改、错校区/session/目标/角色、超期、坏端点/令牌、重复授权、令牌状态、游标、容量与保留期。

## 验证

- 命令：`/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-restore`
- 结果：完整检查集 **65/65** 通过；课堂事件协议检查通过。
- 当前阶段只完成 Core 协议与内存缓冲；Teacher HTTPS API、Agent loopback 接口、Teacher 课堂生命周期接线、Companion 客户端及手机/桌面事件 UI 尚未完成。学生目前还不能实际举手，不能把本记录理解为双向通道已可用。
- Windows SYSTEM/HTTP.sys、真实 TLS pin、局域网、防火墙/VLAN、断网恢复和 24 台并发尚未实测。

## 下一步

按专项规格 [`classroom-event-channel`](../../../specs/classroom-event-channel/tasks.md) 继续接入 Agent、Teacher HTTPS 与 Companion；完成自动化 API/端到端检查后，再为这段集成单独提交。实机验收另行记录，不以本机协议检查替代。
