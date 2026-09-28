# 学生虚拟机网站 Agent 启动失败排查记录

记录日期：2026-09-28。用于汇总学生虚拟机重置后反复部署失败的现象、已取得的证据、尝试过的代码处理及结果，避免把尚未证实的推断当成结论。

## 当前结论

- **2026-09-28 0.4.24 推送修正：用户确认空配置学生机可部署，但教师连接 `DESKTOP-FGVQHIA:39174` 超时。** 源码检查发现旧防火墙规则绑定 Agent EXE，而 `HttpListener` 在 Windows 上通过 HTTP.sys 接收网络请求。0.4.24 改为 `System` 程序过滤，仍仅允许 TCP 39174、LocalSubnet 来源，适用所有网络类别。此项已通过真实 Windows 防火墙规则回归；尚未完成两台 VM 间实际推送验收，不能仅凭截图认定所有超时均由这一原因造成。
- 本次按用户要求暂不实现“已有配置先删除再导入”。同校区部署时更新本工具的防火墙规则，不删除学生配置或更换信任公钥。
- **2026-09-28 代码修复更新：已复现安装器的文件权限缺陷，并完成 0.4.23 修正。** 在 Windows 临时目录运行旧版相同的 `icacls /inheritance:r /grant:r ... /T /C` 命令，退出码为 0，但 DLL 的 DACL 读回为 `D:PAI`，没有任何允许访问的条目。SYSTEM 因而无法加载该文件。这与历史截图中“目录有权限，DLL 后没有权限条目”一致。
- 0.4.23 已去掉递归套用目录权限的命令，改用 Windows ACL API，分别设置目录和文件的明确权限，再逐项读回。新安装、已存在目录修复、重复部署、配置权限修复及真实发布 Agent 加载检查均通过。**重置学生虚拟机后的 SYSTEM 启动、教师连接和黑名单推送仍需用新包确认，尚未宣称现场问题全部解决。**
- Veyon 安装状态、公钥认证方式、公钥指纹和 `VeyonService` 均读回成功。失败范围是学生端独立网站策略 Agent。
- 0.4.21 学生机上，SYSTEM 计划任务的 `LastTaskResult` 是 `0x80070005`（拒绝访问），任务当前状态为 `Ready`；39174 没有监听，未找到 Agent 启动日志。任务身份读回为 SYSTEM，显示出的代理目录及配置 ACL 给 SYSTEM／Administrators 完全访问。
- 当时这些证据只能定位到任务启动中的访问拒绝；本次新增的 Windows 回归已证实安装器能够自行清空文件访问权限。其他现场因素是否同时存在，仍以新包部署结果为准。
- 0.4.22 曾把 Agent 移到 Program Files，但本机部署在 `.staging-*` 目录阶段报“拒绝访问”，部署后检查也找不到正式 Agent 文件。这个版本的路径调整没有解决当前 VM 的问题。
- 不应把“等待时间太短”或“防火墙”写成本次文件加载失败的根因。初始健康请求超时只是没有本机 `/health` 响应的表现；已有 Public 网卡的受限防火墙修正仍保留。

## 排查时间线

### 1. 初始部署结果

截图显示学生端完成了 Veyon 密钥认证配置，公钥指纹匹配，Veyon 服务正在运行。`website-agent` 步骤则报告 SYSTEM 任务和防火墙规则已登记，但没有收到本机代理健康响应，部署整体为 `needs-review`。

因此，Veyon 的成功读回不能代表网站 Agent 已运行。防火墙规则“已登记”也不能代表 Agent 已启动或正在监听。

### 2. 确认故障与虚拟机重置相关

用户确认每次重置学生虚拟机、重新导入校区包都会复现；过去通过的多项检查没有阻止它再次出现。故障需要按“重置后的新部署冷启动”处理，不能只根据成功构建或静态测试认定已修复。

### 3. 读取计划任务、监听端口和日志

在学生 VM 的管理员 PowerShell 读取到：

| 项目 | 读取结果 |
| --- | --- |
| 任务身份 | `SYSTEM` |
| Agent 路径 | `C:\ProgramData\VeyonCampus\WebsitePolicyAgent\0.4.21\VeyonCampus.Agent.exe` |
| 任务参数 | `--website-policy-agent` 加上 ProgramData 中校区配置 JSON 路径 |
| 上次运行时间 | `2026-09-28 16:29:29` |
| 上次任务结果 | `2147942405`，十六进制为 `0x80070005`（拒绝访问） |
| 任务状态 | `Ready`，没有处于运行状态 |
| TCP 39174 | 没有监听结果 |
| `agent-startup.log` | 搜索结果为空 |
| 显示出的目录和配置 ACL | SYSTEM／Administrators 有完全访问；Users 为读取或读取执行 |

执行过的只读读取逻辑如下：

```powershell
$task = Get-ScheduledTask -TaskName 'VeyonCampus-WebsitePolicyAgent'
$info = Get-ScheduledTaskInfo -TaskName 'VeyonCampus-WebsitePolicyAgent'
$exe = $task.Actions[0].Execute.Trim('"')
$config = [regex]::Match($task.Actions[0].Arguments, '--website-policy-agent\s+"([^"]+)"').Groups[1].Value

[pscustomobject]@{
    UserId = $task.Principal.UserId
    Execute = $exe
    Arguments = $task.Actions[0].Arguments
    LastRunTime = $info.LastRunTime
    LastTaskResult = ('0x{0:X8}' -f $info.LastTaskResult)
} | Format-List

icacls $exe
icacls (Split-Path -Parent $exe)
icacls $config
icacls (Split-Path -Parent $config)

Get-NetTCPConnection -LocalPort 39174 -State Listen -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort, OwningProcess

Get-ChildItem "$env:ProgramData\VeyonCampus\WebsitePolicy" -Filter agent-startup.log -File -Recurse -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName; Get-Content -LiteralPath $_.FullName -Tail 20 }
```

这一步没有修改系统设置。截图里的 Task Scheduler Operational 事件查询没有显示匹配事件。

### 4. 0.4.22 Program Files 路径尝试及结果

根据 `0x80070005` 和 0.4.21 Agent 位于 ProgramData 的证据，0.4.22 曾尝试把版本化 Agent 安装到：

```text
C:\Program Files\VeyonCampus\WebsitePolicyAgent\0.4.22\VeyonCampus.Agent.exe
```

校区配置仍在 ProgramData。0.4.22 的 x64 学生包已生成，嵌入式 Veyon 安装器校验通过。

在用户本机部署 0.4.22 时，`website-agent` 报告：

```text
Access to the path
'C:\Program Files\VeyonCampus\WebsitePolicyAgent\0.4.22.staging-...'
is denied.
```

部署后只读检查也报告找不到普通文件形式的独立 Agent。由此确认：当前环境中 Program Files 暂存安装失败，0.4.22 这项路径调整没有完成 Agent 安装。不要把 0.4.22 的 Program Files 路径尝试描述为已修复。

## 代码处理记录

### 0.4.22 中已存在的诊断改动

- 健康探测明确禁用系统代理，避免 loopback 请求被转发。
- 部署失败结果附加计划任务摘要及本次新增的 Agent 启动异常日志。
- Agent 捕获启动异常时记录完整异常详情，不再只记录异常类型。
- 上述改动已经包含在 0.4.22 源码和学生 ZIP 中；实际部署被 Program Files 暂存目录拒绝访问阻断。

### 原定下一项源码修正（已由下方 0.4.23 实现接续）

结合 0.4.22 暂存目录失败和 0.4.21 的任务访问拒绝，下一次代码修改应：

1. 不再把 Agent 安装到 Program Files；恢复到 ProgramData 下的受保护版本目录，配置继续放在 ProgramData。
2. 对复制出的每个 Agent 文件显式调用文件 ACL 设置，至少明确授予 SYSTEM 和本机 Administrators 完全访问，并给普通 Users 读取权限；不能只依赖目录继承或带 `/C` 的递归 ACL 命令。
3. 对已存在的版本目录也逐个修复并核验文件 ACL，然后再注册／启动 SYSTEM 任务。
4. 保留失败结果中的 Task Scheduler 最后运行码和 Agent 完整启动异常，避免再次只得到“检查任务和防火墙”的泛化提示。
5. 提升 Agent 版本号并生成新学生包。在一个重置后的学生 VM 做完整部署确认：Agent 文件存在、SYSTEM 任务运行中、本机 39174 健康端点响应，然后再验证教师端策略推送。常驻任务的运行结果可能为 `0x41301`（正在运行），不能要求它必须为 0。

### 0.4.23 已落地的处理

1. Agent 恢复安装至 `C:\ProgramData\VeyonCampus\WebsitePolicyAgent\0.4.23`。根目录、版本目录和每一个子目录分别保护；校区配置继续在 ProgramData。
2. 新增 `AgentFileSecurity`，为目录及每个文件生成明确、受保护的 DACL。SYSTEM／Administrators 完全访问；Users 对程序文件只读执行，对配置文件只读。去掉旧的递归 `icacls` 调用，不再以命令退出码代替权限核验。
3. 在复制后、暂存目录正式更名前校验每个文件的权限和内容。若版本目录已存在，先修复目录及所有文件的权限，再比较内容；内容不一致仍停止，避免覆盖未知程序。
4. 已有校区配置即使字节内容完全相同，也重新设置并核验权限。重解析点继续拒绝；权限错误在部署结果中暴露，不继续注册一个无法读取程序的任务。
5. 保留 SYSTEM 计划任务、限定程序／端口／LocalSubnet 的 Any-profile 防火墙规则、本机健康响应检查及完整启动诊断。

### 本次验证结果

- 解决方案构建通过，0 警告、0 错误；0.4.23 学生端自包含发布与内嵌 Veyon 安装器校验通过。
- Windows 管理员临时目录回归 7 项通过：复现旧版空文件 DACL、新装与暂存更名、空 DACL 修复与重复部署、未变配置权限修复、拒绝不同内容、拒绝链接目录、真实发布 Agent 的复制与运行时加载。
- 最后一项使用新包内的实际 EXE/DLL，在安装器复制、保护后直接启动无参数 Agent，按预期返回 2。这证明程序及运行库可以加载；该检查没有注册 SYSTEM 任务或启动网络监听。
- 学生端专项检查 5 项通过。完整测试集在第 2 项中被旧断言阻断：测试要求 `VeyonAuthKeyId.ForCampus("校区一")` 抛错，而现有代码允许中文校区名；本次没有更改该功能或宣称完整测试通过。
- 验证入口：`tests/VeyonCampus.Checks` 的 `--agent-installation-fixtures`；只用于开发回归，学生端日常部署不需要执行它。

### 交付与现场确认

新包：`artifacts/VeyonCampus-0.4.23-student-setup-win-x64.zip`。

ZIP SHA-256：`D3C6835296C03E849A6325FCC66DE7B9C869AF57066B7A20870538DD6918EF2A`。

在重置后的学生虚拟机解压新包，以管理员身份打开 `VeyonCampus.StudentSetup.exe`，导入原有校区配置包并运行部署。程序自动完成权限设置、已有文件修复、任务启动和健康检查；之后使用界面的“部署后只读检查”，再由教师端推送黑名单。整个正常流程不需要再逐条执行 CLI 修复命令。

用户现场的重置 VM 全流程尚未由本次开发回归代替；现场验证通过后再将问题标记为完成。

## 0.4.24 教师推送超时修正

### 依据及修改

- 新现场：学生端空配置导入成功，教师端显示代理确认 0/1、需核对 1，连接电脑名的 39174 端口超时。该截图不能区分名称解析、网络路由与防火墙丢包。
- 旧实现 `EnsureFirewallRule` 使用 `program=<Agent EXE>`；接收端使用 `HttpListener`。微软说明 [HttpListener 使用 HTTP.sys](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=net-10.0)，[防火墙规则编写说明](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ics/general-firewall-rule-authoring-process)要求按真正持有网络套接字的组件设置规则，驱动程序对应 `System`。
- 新增 `WebsitePolicyFirewall`，通过 Windows Firewall COM API 设置并读回结构化属性，避免解析中英文 netsh 输出。程序过滤改为 `System`，保留入站、TCP 39174、LocalSubnet、Profile Any。
- 可以识别旧版已知 Agent 路径和新版带本工具标识的 System 规则。所有同名规则先检查归属，发现未知规则即停止；支持重复部署更新。不会关闭防火墙或开放任意来源。
- 部署后只读检查新增防火墙规则核验；本机健康响应不再单独作为网络规则已正确登记的依据。此检查仍不保证教师网络到学生机的实际可达性。

### 验证与待验收

- Windows 防火墙专项 4 项通过：新建规则属性读回、重复部署、旧 EXE 规则被核验拒绝且可升级、未知同名规则保持不变。测试使用随机名称临时规则并在结束时删除，不触及正式规则。
- 学生端专项 5 项通过；解决方案构建 0 警告、0 错误。未宣称完整历史测试集通过。
- 验证入口：`tests/VeyonCampus.Checks --agent-firewall-fixtures`，需要管理员权限。
- ZIP SHA-256：`6F455E1564D55F89526F1D299A93A5AC912DDC10295ADD47411B6A5C5C552E8F`。
- 交付：`artifacts/VeyonCampus-0.4.24-student-setup-win-x64.zip`。教师端无需因这条规则修正更换程序。在学生端使用新包和原校区配置执行部署，再进行只读检查和教师推送。
- 尚待现场验收：教师端确认 1/1、学生端实际执行策略。如果仍超时，先用学生 IPv4 地址推送，区分电脑名解析与网络问题，再检查双方是否在允许通信的本地子网。无需先删除原有校区配置。

## 相关位置

- Agent 安装、ACL、任务注册和健康读回：`src/VeyonCampus.Core/WebsitePolicyRuntime.cs`
- Agent 进程入口与启动异常记录：`src/VeyonCampus.Agent/Program.cs`
- 现有通用检查步骤及历史 VM 回传：`docs/学生机策略代理故障检查步骤.md`
- 2026-09-28 生成但未解决该 VM 问题的包：`artifacts/VeyonCampus-0.4.22-student-setup-win-x64.zip`
