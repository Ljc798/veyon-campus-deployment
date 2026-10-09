# 双向课堂事件通道：Teacher API

日期：2026-10-10（Asia/Hong_Kong）

## 本阶段交付

Teacher HTTPS 服务现在提供当前课堂事件的服务端基础：

- 仅当校区、活动 session、目标电脑均匹配且该 Agent 公钥已固定时，Teacher 才签发短期授权。服务端仅在内存保存令牌摘要，并绑定 Agent 指纹和教师 HTTPS 叶证书 SHA-256。
- 学生提交事件时必须持有本堂课授权，并使用目标 Agent 已固定私钥签名；事件再次提交按 ID 去重。每台电脑每分钟最多提交 10 次。
- 已配对手机沿用现有访问令牌和请求随机数读取同一课堂事件流，并可以回复对应学生求助。
- Teacher 转发事件原始签名封装，保留发送者身份；不会用教师密钥重新签署学生消息。
- 长轮询最多等待 25 秒；结束课堂会取消未完成等待。事件仍只保存在有界内存队列中。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore`：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：完整检查 **65/65** 通过。API 覆盖配对手机取求助、签名回复、学生取回原签名事件、伪造 Agent 密钥拒绝、重复事件去重、每台 10 次/分钟限速、Agent 身份撤销和下课中断长轮询。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole`：0 警告、0 错误。

## 尚未接通

Teacher API 暂时还没有接入 Teacher 窗口的课堂生命周期；Teacher 尚未把授权推送到 Student Agent，Companion 也尚未通过 loopback 发起求助或收取回复。因此本阶段自动检查证明的是服务端 API 合同，不代表学生端到教师端已经可用，也不替代 Windows 双机、手机、LAN/VLAN、防火墙和断网恢复实测。下一步接入 Agent 独立授权端点与本机 loopback，再把授权和事件读取纳入课堂生命周期。
