# 课堂状态同步 S1-03 实现记录

日期：2026-10-10

环境：macOS arm64，.NET SDK 10.0.401。

版本候选：TeacherConsole/StudentSetup/Worker/Student Companion **0.4.59**，Student Agent **0.4.44**，Core **0.4.43**。

范围：Teacher 开始/下课、Teacher→Agent 签名状态推送、Companion 本机读取。没有发布 tag、Release 或安装器。

## 实现结果

- Teacher 课堂卡片只有一个开始/下课按钮。它沿用已保存的校区和机房；没有历史选择时使用第一条保存档案。电脑地址来自机房已有主机覆盖项，未填写时使用电脑编号。活动课堂自动每 30 秒刷新，不增加连接参数、目标表单或手动重试按钮。
- 本次课堂使用的签名校区 ID 以 session ID 关联保存在教师机的独立小型上下文文件中；教师切换到其他页面或校区时，不会让活动课堂换用另一把签名身份。文件只含校区 ID 和 session ID，不含密钥；旧版 Teacher 忽略该文件，原课堂记录格式不变。
- Teacher 复用已部署的 TCP 39174 端口和现有校区签名信任；课堂消息使用独立路径与 `VeyonCampus.ClassroomStatus.v1` 用途。已固定的 Agent 身份回执才算确认；未固定密钥显示需核对。
- Student Agent 独立验证签名、校区、用途、时间、字段和消息单调性，只把最新状态放在进程内存中。它不调用策略执行器、不写注册表或持久文件。Agent 重启后状态为空，教师下一次 30 秒刷新重新同步。
- Companion 每 5 秒从 `127.0.0.1:39174/v1/classroom/status/local` 读取 Agent 签名快照。此端点仅接受 loopback。断网、Agent 不可用、验签失败或两分钟过期后，界面自动回到“等待教师端连接”。
- 状态卡继续只显示必要信息；不添加 WSS 监听、证书引导、服务发现、CloudBase 中继、连接配置、重试选项或额外窗口。
- Teacher 操作状态现在区分本机课堂记录未更改与本机记录已更新但学生端同步未完成，避免网络失败提示错误地宣称本地操作已保存。

## 主要文件

- [课堂状态签名协议与传输](../../../src/VeyonCampus.Core/ClassroomStatusProtocol.cs)、[签名上下文存储](../../../src/VeyonCampus.Core/ClassroomSigningContextStore.cs)、[Agent LAN 与 loopback 端点](../../../src/VeyonCampus.Core/WebsitePolicyRuntime.cs)
- [Teacher 操作与单按钮界面](../../../src/VeyonCampus.App/TeacherViewModel.cs)、[TeacherWindow.axaml](../../../src/VeyonCampus.App/TeacherWindow.axaml)
- [Companion 状态轮询器](../../../src/VeyonCampus.Companion/StudentCompanionStatusPoller.cs)、[Companion 生命周期](../../../src/VeyonCampus.Companion/App.axaml.cs)
- [课堂状态需求](../../../specs/classroom-live-channel/requirements.md)、[设计](../../../specs/classroom-live-channel/design.md)、[实施任务](../../../specs/classroom-live-channel/tasks.md)

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx -c Release --no-restore -m:1 -nr:false`：通过，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-build --no-restore`：64/64 通过；覆盖签名篡改、重复/未知字段、用途/校区隔离、重放拒绝、两分钟过期、Agent 回执、loopback、签名上下文匹配/清理/损坏保留及 Companion 状态映射。
- TeacherConsole、StudentSetup 均按 win-x64、locked restore 与 self-contained 发布成功。角色元数据为 0.4.59；StudentSetup 目录含 `WebsitePolicyAgent/VeyonCampus.Agent.exe` 与 `StudentCompanion/VeyonCampus.StudentCompanion.exe`，TeacherConsole 不含 Companion；四个入口均识别为 Windows x64 PE。
- `git diff --check` 在提交前执行。

本次交叉发布是普通发布目录，不是 Inno Setup 安装器。当前环境不能验证 Windows HTTP.sys/SYSTEM 运行、真实校园网双机请求、loopback 路由、安装/升级/卸载、登录启动和托盘交互。需在 Windows 上继续验收：教师开始/下课后学生状态更新、断网与 Agent 重启后的恢复、两分钟 TTL、安装覆盖和卸载清理。64 项自动检查和交叉发布都不能替代这些现场测试。
