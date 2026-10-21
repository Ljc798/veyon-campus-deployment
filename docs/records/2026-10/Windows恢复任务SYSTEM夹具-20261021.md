# Windows 恢复任务 SYSTEM 夹具

日期：2026-10-21（Asia/Hong_Kong）

## 实现

- 在现有 Windows Agent 重配置夹具首次安装成功后，创建一个一次性的 SYSTEM 任务启动测试子进程。
- 子进程调用生产 StudentUpdateAtomicTransaction.RegisterRecoveryTasks，验证 SYSTEM 身份、开机恢复任务与每分钟 watchdog 的可执行文件/配置绑定、任务 ACL、开机触发器、watchdog 一分钟重复间隔和受控清理。
- 在 SYSTEM 子进程内先持有生产更新互斥锁，再从另一线程调用学生 Agent 健康修复处理器；必须得到 `BusyUpdating`，并确认没有写入该命令的持久化回执。GitHub workflow 将 `healthRepairUpdateLockRejected` 作为必需证据。
- 请求与结果保存在 ProgramData 下单独创建的 SYSTEM/Administrators 私有目录；任务名使用随机 GUID，夹具只在现有 Agent 校区配置和已安装 Agent 路径匹配时运行。
- workflow 保留独立 JSON artifact；失败会报告阶段和 HRESULT，进程启动失败时生成兜底 JSON。报告标记 g0Acceptance=false。

## 验证状态

- 当前本机 macOS arm64 可用 .NET SDK 10.0.401；健康协议/UI 专项检查已编译并通过。该 Windows 专用 SYSTEM 夹具只在 Windows 上运行，本机未执行。
- Windows runner 尚未运行包含本次变更的工作流；新的 artifact、mutex 争用检查与 SYSTEM Task Scheduler 行为仍待实测。

## 门槛与限制

- 夹具验证生产任务注册/读回代码在托管 Windows runner 上的 SYSTEM 上下文中可执行；它没有模拟学生更新事务，也没有触发真实开机恢复。
- 它不覆盖 Windows 10/11 实机故障注入、断电/重启、完整安装器失败和 Teacher—Student 双机验收，G0 仍未通过。
