# 课堂负载基线工具实现记录

日期：2026-10-10（Asia/Hong_Kong）

## 本次完成

为路线图中的课堂负载验收补齐两种互相独立的数据来源：本机 HTTPS API 模拟检查覆盖 10、24、70 个目标并输出提交延迟 P50/P95/最大值及事件投递往返时间；Windows 现场采集脚本每秒采样 TeacherConsole CPU、工作集/私有内存和所有网卡吞吐，供 10、24、60–70 台真实 LAN 测试时使用。

自动检查输出 `measurementScope=synthetic-local-https-api`，不会伪装成校园网数据。现场脚本只把活动课堂的目标数写进报告，不输出电脑编号、课堂名称、会话 ID 或设备地址；网络指标是全系统网卡的累计流量差值，包含教师机其他应用的数据。没有设置性能通过门槛。

## 实现位置

- `tests/VeyonCampus.Checks/MobileControlApiChecks.cs`：测量 10/24/70 并发提交和事件送达；教师端超过固定 50 条分页时按游标读取完整事件流。
- `tests/VeyonCampus.Checks/Program.cs`：更新集成检查名称。
- `scripts/collect-classroom-load.ps1`：Windows 现场资源采集，默认 120 秒，报告放在被 Git 忽略的 `artifacts/pre-release-test/`。
- `specs/classroom-event-channel/` 与 `docs/测试验收与发布清单.md`：记录数据范围、使用方法和真实现场验收边界。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1 -p:VeyonCampusIncludeInstaller=true`：成功，0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release -p:VeyonCampusIncludeInstaller=true`：**67/67 检查通过**；集成检查成功输出 10、24、70 目标的模拟指标。
- `git diff --check`：通过。
- 当前开发环境没有 PowerShell，现场采集脚本尚未在 Windows 执行；本记录不把脚本静态检查描述成实机验证。

## 验收边界

模拟 API 在开发机进程内通过 HTTPS 运行；其延迟受开发机、RSA 运算和测试负载影响，只用于自动回归比较。它不证明 Windows HTTP.sys/SYSTEM、真实手机、真实学生机、Wi-Fi/VLAN/防火墙或校园网络容量。真实课堂负载仍需在 Windows 运行采集器，并记录课堂配置目标数与实际在线参与数。
