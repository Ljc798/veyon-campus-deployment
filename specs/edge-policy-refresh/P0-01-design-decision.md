# Edge 策略刷新 P0-01：基线审计与设计决定

日期：2026-10-18  
基线：`develop` 与 `v1.1.0` 均指向 `f230566281f0141e887c6ecad5f43feb05ae7ef0`，工作区开始时干净。

## 基线源码差异清单

- `WebsitePolicyAgent` 已在受保护的 SYSTEM 计划任务中运行，验签后通过注册表事务写入 HKLM Edge/Chrome/Firefox 策略。
- 策略写入后的 Edge 刷新请求只保存在 `StudentAgentEdgePolicyRefreshSignalState` 的进程内存中；Agent 重启会丢失请求。
- `StudentCompanionEdgePolicyRefreshPoller` 每 500 毫秒读本机 Agent 请求，使用 `MainWindowHandle` 寻找 Edge，并把 `Process.Start()` 成功视为已请求；异常没有持久诊断。
- Companion 同时运行一个旧式浏览器重启轮询器，其中包含 `edge://restart` 和键盘注入逻辑。
- Companion 清单为 `asInvoker`，但 Inno 安装后的即时启动使用 `runascurrentuser`；交互安装器提升后可能把提升令牌传给桌面进程。
- Agent 更新本身已有版本目录、健康读回和回滚；本阶段复用，不把 Edge MSI 混入 Agent 自更新。

## P0-01 决策

先不调用 `WTSQueryUserToken` / `CreateProcessAsUserW`。Agent 目前是 SYSTEM 计划任务，不应仅凭 SYSTEM 身份假定令牌权限及进程创建语义在目标机上成立；在真实 Windows 10/11 学生机上验证前，不把该 API 作为成功条件。

采用文档允许的退路：安装一个独立、无 UI、`asInvoker` 的 Edge 策略执行器。受限用户组登录任务负责启动并在崩溃后最多重试三次；All Users Startup 快捷方式作为登录启动兜底。执行器只轮询 Agent 签名的本机刷新信号，并且只向同一 SessionId、同一用户 SID、同一完整性级别下已显示 Edge 窗口的标准 Stable 安装路径发送固定参数 `--refresh-platform-policy`。用户、会话或完整性级别无法匹配时记为待核对，不向 Edge 派发。该程序不提供命令行转发、浏览器路径输入或网络执行接口。

当前精确兼容白名单只包含 `155.0.4283.45`，对应已记录的成功样本；不使用“主版本大于等于某值”的判断。Edge 118 和其他未列入白名单的版本不运行参数，保留 HKLM 策略并记录 `UnsupportedBrowserVersion`。更新白名单必须有新版本实机证据。

刷新结果最高报告为 `DispatchedUnverified`。启动了 Edge 进程只说明请求已发出，不说明 Edge 已读取或实际应用策略。浏览器未运行时保存的 Agent 请求保持待处理，用户正常打开 Edge 后由该用户执行器再次检查；不主动打开浏览器。

## 实机验证状态

本轮源码审计和自动化协议规则可在仓库验证。当前执行环境无法访问校区学生实机，故 P0-01 的 Win10/11 会话执行器登录、Edge 155 实际刷新、Edge 118 不弹窗等实机项目均标记为**未验证**。手工步骤见 `manual-test-guide.md`；不得据此宣称完成实机验收。
