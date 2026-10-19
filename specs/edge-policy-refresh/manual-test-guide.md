# Edge 策略刷新 v1.1.2 实机验收

仅在一台可恢复的测试学生机上先做，不要先批量部署。验收记录 Edge 完整版本、Windows 版本、学生会话、策略 Revision 和结果；不要记录浏览历史或学生访问 URL。

## 测试前检查

1. 安装本地构建的 StudentSetup 1.1.2，确认 Agent 是 SYSTEM 计划任务，Edge Policy Executor 登录任务存在且为受限用户组，Edge Policy Executor 和 Companion 在学生登录会话中运行。
2. 在任务管理器“详细信息”页显示“用户名、会话 ID、提升”列。Edge 与 Edge Policy Executor 必须同一用户、同一会话、同一完整性级别；执行器未提升。Agent 的 SYSTEM 身份是预期行为。
3. 保持 Edge 已打开并访问 `edge://policy`。记录 URLBlocklist/URLAllowlist 当前值、Edge 版本和页面状态；不要手动点“重新加载策略”。
4. 在教师端选择“读取逐台状态”，保存 Agent 版本、已安装 Edge、运行进程版本、SessionId、脱敏 SID、完整性级别、执行器回执和更新服务状态；再运行只读诊断脚本 `scripts/Test-EdgePolicyRefresh.ps1`，保存第一次输出。

## 验收步骤

| 编号 | 场景与操作 | 期望 | 现场结果 |
|---|---|---|---|
| A01 | Edge `155.0.4283.45` 已打开，教师端推送新增测试黑名单 | 不出现新窗口、不关闭或重启；Agent Revision 变化；执行器最多记录 `DispatchedUnverified`；edge://policy 与新导航分别人工确认 | 未验证 |
| A02 | 推送解除/修改黑白名单 | 不重启；新的策略值及后续导航符合预期 | 未验证 |
| A03 | Edge 118 已打开并推送策略 | 不运行刷新参数、不弹新窗口；策略保留，诊断为版本不支持 | 未验证 |
| A04 | 完全退出 Companion 后推送策略 | Agent 仍接收并持久化队列；独立执行器仍触发刷新 | 未验证 |
| A05 | Edge 关闭时推送策略，再正常打开 Edge | 不主动启动 Edge；请求保持待处理，用户打开后再处理；自然启动读取 HKLM 策略 | 未验证 |
| A06 | 分别测试策略根键已存在、首次创建后推送 | 分开记录策略列表、页面读回及新导航结果；不能把注册表写入当作页面已应用 | 未验证 |
| A07 | Agent 重启/更新后检查当前 Revision | 未完成的最新队列恢复；过期请求不会无限重复打开浏览器 | 未验证 |
| A08 | 管理员安装后当前用户立即登录/启动 | Companion 与 Executor 运行于原始普通用户令牌，不继承安装器提升 | 未验证 |
| A09 | Edge 使用“以管理员身份运行”打开，执行器保持普通用户启动 | 完整性级别不匹配时显示 `PendingSession / EDGE_INTEGRITY_MISMATCH`，不启动刷新参数 | 未验证 |
| A10 | 多用户/RDP 会话与多个 Edge Profile | 执行器只处理自身用户和会话；无法确认目标时安全跳过 | 未验证 |
| A11 | 检查 Chrome 课堂功能、Companion 通知与求助 | 未回归；Chrome 的独立历史兼容路径仍保持，Edge 不再被 restart 流程触发 | 未验证 |
| A12 | 在测试机结束 Edge Policy Executor 进程 | 任务计划程序最多每分钟重启三次；同一用户不出现重复执行器，Agent 队列不丢失 | 未验证 |
| A13 | 静默升级或卸载学生端 | 升级后由下一次用户登录启动新执行器；卸载前删除登录任务，卸载后不再启动执行器 | 未验证 |

每个策略操作后在教师端重新读取逐台状态，并另运行诊断脚本保存第二份输出。诊断结果并列区分机器磁盘安装版本、运行进程映像版本、Agent 队列和各用户会话的执行器报告。用户态本地执行器报告只作定位线索；`DispatchedUnverified` 不是浏览器已生效证明。以 `edge://policy` 列表和重新导航的人工结果作为现场证据。若两者不同，附上系统版本、Edge 完整版本、进程 SessionId/完整性级别和执行器日志，不要附浏览历史或访问 URL。

## 只读诊断

在学生普通 PowerShell 中运行：

```powershell
.\scripts\Test-EdgePolicyRefresh.ps1
```

脚本不写注册表、不启动/关闭 Edge、不发送策略。策略名单只输出数量和 SHA-256，不打印域名。
