# Windows 原子更新夹具 CI 实测

日期：2026-10-21（Asia/Hong_Kong）

来源：[GitHub Actions Windows Installers run 39566852692](https://github.com/Ljc798/veyon-campus-deployment/actions/runs/39566852692)

源码提交：`7caf1a7eb0a661e9bcdd507b0bb5c44a37fc2567`。运行环境：GitHub 托管 Windows Server 2025（10.0.26100.0，`win25-vs2026`）。`build-installers` job 通过；PowerShell 5.1 G0 采集器解析、文件清单盘点、Student/Teacher 安装器构建与安装/卸载步骤均成功。

## 夹具读回

- `VeyonCampus.StudentUpdateRecoveryTaskFixture`：`status=passed`、`g0Acceptance=false`。SYSTEM 子进程确认了恢复/watchdog 任务注册与读回、SYSTEM 身份、任务动作/DACL、开机触发器、watchdog 一分钟调度和清理；更新互斥期间健康修复返回 `BusyUpdating`，且没有持久化修复回执。
- `VeyonCampus.StudentUpdateAtomicStorageFixture`：`status=passed`、`g0Acceptance=false`。确认了受保护 journal ACL、陈旧 journal 状态拒绝、NTFS 同卷旧目录保留与恢复、未知状态拒绝，以及损坏 journal 时保留恢复材料。
- 两份 JSON 报告作为 `student-update-system-task-fixture-39566852692` 和 `student-update-atomic-storage-fixture-39566852692` artifacts 上传。完整 role inventory 作为 `read-only-file-update-inventory-39566852692` 上传。

## 验收边界

这次证据证明对应的 Windows runner 夹具已执行通过，扩展了此前只有 macOS 可移植检查的证据。它不证明 G0 通过：两个 artifact 都明确 `g0Acceptance=false`，环境也不是 Windows 10/11 教师/学生设备。该 run 没有完成真实安装器中断/失败注入、各 journal 阶段强制重启、断电恢复、真实双机 LAN 更新或 Teacher 新鲜签名身份读回。现场矩阵中这些案例仍须逐项记录，性能 P50/P95 仍需真实教室基线。
