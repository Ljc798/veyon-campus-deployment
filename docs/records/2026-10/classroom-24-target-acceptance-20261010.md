# 课堂事件 24 目标并发自动验收记录

日期：2026-10-10（Asia/Hong_Kong）

## 本次完成

补齐产品路线图 S1-08 中可在本机自动验证的 24 台并发、断线重连和过期课堂会话检查。检查经本地 Teacher HTTPS API 运行，复用真实 Kestrel 路由、授权、签名验证、事件缓冲和长轮询逻辑；不调用 CloudBase，不新增用户设置或操作选择。

测试为 24 个独立目标生成不同 Agent RSA 身份、固定公钥和课堂授权。每个目标同时开始长轮询后取消请求，再用相同授权和游标重连；随后分两轮并发提交求助事件。检查确认：

- 每个学生目标只能读到自己的签名事件；重复连接从各自游标继续，不重收上一轮事件。
- 教师课堂事件流完整收到 48 条不同事件 ID，目标不串台。
- 结束课堂后 24 个学生授权与等待请求全部失效；手机课堂流不返回旧课堂事件。
- 事件传输由本机 HTTPS Teacher API 处理，不依赖 CloudBase。

同时固定了原移动 API 测试中一个时间边界问题：事件签发时间和过期时间之前分别读取系统时钟，偶尔会让有效期多出几个时钟刻度并触发严格校验。现在使用同一时间基准计算过期时间。

## 实现位置

- `tests/VeyonCampus.Checks/MobileControlApiChecks.cs`：本地 HTTPS 端到端模拟、24 个身份与重连/会话撤销检查。
- `tests/VeyonCampus.Checks/Program.cs`：将检查名称明确为 24 目标并发与授权边界。
- `specs/classroom-event-channel/requirements.md`、`design.md`、`tasks.md`：记录自动检查范围及与真实设备验收的边界。

## 验证

- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release -p:VeyonCampusIncludeInstaller=true`：完整 **66/66** 检查通过。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：重复完整检查，**66/66** 通过。
- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -p:VeyonCampusIncludeInstaller=true -m:1`：成功，0 警告、0 错误。
- `git diff --check`：通过。

## 验收边界

本记录证明模拟 Agent 经本地 Teacher HTTPS 服务并发时的 API 和内存事件链路表现，不证明真实 Windows HTTP.sys/SYSTEM、实体手机、Companion 登录会话、防火墙/VLAN、校园 Wi-Fi 客户端隔离、真实断网恢复或 24 台实体电脑容量。上述现场验收仍开放；不应把此自动检查作为校区发布门槛的替代。
