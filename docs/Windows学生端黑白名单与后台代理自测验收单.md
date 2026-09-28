# Windows 学生端后台代理与网站限制自测验收单

更新日期：2026-09-27。此清单供维护者在可还原的 Windows 10/11 测试机上执行。当前开发环境是 macOS；本文件中的角色 ZIP、SYSTEM 任务、普通用户权限、注册表策略和浏览器效果尚未实机验收。代码构建或自动检查通过不能替代这些步骤。

## 1. 测试前准备

- [ ] 使用可恢复的 Windows 10 x64 或 Windows 11 x64 虚拟机／专用测试机，先建立快照。不要在生产课堂电脑或日常使用的电脑上测试。
- [ ] 测试网络能访问两个学校批准的测试网站；记录它们的域名，避免使用学生真实学习记录或敏感网址。
- [ ] 准备一台测试教师电脑和一台学生电脑，或两台彼此隔离的测试虚拟机；准备教师账号、学生标准账号和管理员账号。学生账号不得属于本机 Administrators 组。
- [ ] 在教师 App 中生成新的 `schemaVersion=3` 学生校区配置包，并记录校区 ID。确认包内只有公钥，没有网站签名私钥。
- [ ] 在 Windows 开发机从当前源码生成学生部署 ZIP：

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\scripts\package-windows-offline.ps1 `
    -Role StudentSetup `
    -OutputDirectory artifacts\windows-x64-v0.4.18-student-runtime-test `
    -ZipPath artifacts\VeyonCampus-0.4.18-student-runtime-test.zip
  ```

  需安装 .NET 10 SDK。打包结束后记录 ZIP 的 SHA-256；测试时解压整个 ZIP，不要只复制 EXE。

- [ ] 在 Windows 开发机使用同一源码另外生成教师控制台 ZIP：

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\scripts\package-windows-offline.ps1 `
    -Role TeacherConsole `
    -OutputDirectory artifacts\windows-x64-v0.4.18-teacher-runtime-test `
    -ZipPath artifacts\VeyonCampus-0.4.18-teacher-runtime-test.zip
  ```

- [ ] 核对两个 ZIP 的 SHA-256 和 `veyon-campus-role.json`：学生包角色为 `StudentSetup`，入口为 `VeyonCampus.StudentSetup.exe`，包含 `WebsitePolicyAgent`；教师包角色为 `TeacherConsole`，入口为 `VeyonCampus.Teacher.exe`，不含 `WebsitePolicyAgent` 或 `VeyonCampus.StudentSetup.*` 文件。学生包不得包含 `VeyonCampus.Teacher.*` 教师产物。
- [ ] 分别解压到独立目录并双击两个入口。学生工具只出现学生部署功能；教师控制台只出现 Veyon Master 安装、校区配置包生成和网站策略控制。不得以共用窗口中的默认页或隐藏导航作为隔离证据。
- [ ] 核对两种 exe 的文件图标和运行时窗口图标：应显示新盾牌／屏幕／书页标识。检查各自的标题区、导航、卡片和按钮在 100%、125%、150% 缩放下没有遮挡或文字截断。

记录本次测试信息：

```text
日期／测试人：
App 版本／源码提交／ZIP SHA-256：
Windows 版本、版本号、构建号、架构：
Edge 版本：
Chrome 版本：
测试机名／测试校区 ID：
快照名称／恢复方式：
```

## 2. 学生部署与只读检查后清理 GUI

在快照中的学生测试机，以管理员身份运行解压目录中的 `VeyonCampus.StudentSetup.exe`，载入对应校区配置包并按计划部署。若修改后要求重启，重启后重新打开学生部署工具并载入同一配置包。

- [ ] App 的“部署后只读检查”确认 Veyon 版本、认证公钥和 VeyonService 状态。
- [ ] 如果配置包包含网站策略公钥，检查同时确认独立 Agent 文件、配置校区、公钥、SYSTEM 开机任务、本机健康响应以及任务 ACL；任一项未确认时，清理按钮应禁用。
- [ ] 只读检查通过后按“检查通过后退出并清理部署工具”。窗口关闭后，检查当前解压目录中的 `VeyonCampus.StudentSetup.exe`、发布清单和发布标记已移除；如果目录还含未列入清单的学校文件，确认这些文件仍在。
- [ ] 确认 `C:\Program Files\VeyonCampus\WebsitePolicyAgent\0.4.22\VeyonCampus.Agent.exe` 仍存在；学生包目录中的便携 Agent 文件可以被清理，Program Files 中的常驻副本不能被清理。
- [ ] 确认 `VeyonCampus.StudentSetup.exe` 已不存在，双击该路径无法打开学生部署页面；教师控制台 ZIP 从未复制到学生测试机。
- [ ] 双击 Program Files 中的 `VeyonCampus.Agent.exe`。预期没有 GUI 页面或控制台窗口；标准用户手动启动不应启动策略服务，因为 Agent 只接受 SYSTEM 身份。
- [ ] 管理员重新检查 VeyonService 和 Agent 任务仍在运行。记录清理前后的路径、截图和只读查询结果。

清理功能只删除发布脚本写入文件清单的便携包文件；它不会卸载 Veyon、停止 SYSTEM Agent、删除策略公钥或还原浏览器设置。

## 3. SYSTEM 任务与普通学生用户权限

先以管理员身份检查计划任务和 ACL：

```powershell
Get-ScheduledTask -TaskName 'VeyonCampus-WebsitePolicyAgent' |
  Select-Object TaskName, State

$service = New-Object -ComObject Schedule.Service
$service.Connect()
$task = $service.GetFolder('\').GetTask('\VeyonCampus-WebsitePolicyAgent')
$task.GetSecurityDescriptor(7)
```

验收要求：任务身份为 `SYSTEM`；任务命令指向 Program Files 中版本目录下的 `VeyonCampus.Agent.exe`，参数为 `--website-policy-agent` 和 ProgramData 中对应校区配置；任务 DACL 是受保护 ACL，只给 `SYSTEM` 与本机 Administrators 完整访问权，不含 Users、Authenticated Users 或 Everyone 的访问 ACE。App 的部署后只读检查也必须通过同一 ACL 核对。

切换到普通学生账号，在非管理员 PowerShell 中逐项尝试：

```powershell
schtasks.exe /End /TN VeyonCampus-WebsitePolicyAgent
schtasks.exe /Change /TN VeyonCampus-WebsitePolicyAgent /Disable
Stop-Process -Name VeyonCampus.Agent -Force -ErrorAction Stop
```

- [ ] `schtasks /End` 和 `/Change /Disable` 被系统拒绝访问；任务仍启用并运行。
- [ ] `Stop-Process` 无法结束 SYSTEM Agent。若命令提示找不到进程，不能据此判定通过；请由管理员先确认任务确实在运行，再重试。
- [ ] 切回管理员账号，只读确认任务仍处于预期状态，Agent 健康端点正常。

普通学生用户不能停止任务或结束 SYSTEM 进程才算通过。本机管理员仍可停止、修改或卸载 Agent；这属于本机管理员权限边界，不能宣传为管理员也无法停用。

## 4. 黑名单：阻止列出的网站

确认 Edge 和 Chrome 在干净快照中均无现有 `URLBlocklist`／`URLAllowlist` 机器策略。教师端打开“学生网站黑名单 / 白名单”：

1. 填写与学生包完全相同的校区 ID。
2. 填写这台学生机的电脑名或 IP；本轮先只测一台。
3. 选择“黑名单：只阻止所列网站”，时长选择 45 分钟，填写一个可正常访问的测试域名。
4. 点击“签名并推送策略”，记录策略版本、到期时间和逐台回执。

- [ ] 教师端显示该学生机“代理已确认”。“代理已确认”表示学生 Agent 返回应用结果，不等于浏览器网页本身已验证。
- [ ] 学生机 Edge 打开 `edge://policy`，点“重新加载策略”；Chrome 打开 `chrome://policy`，点“重新加载策略”。确认 URLBlocklist／URLAllowlist 的来源、状态和列表值符合预期。
- [ ] 以管理员 PowerShell 只读核对注册表列表：

  ```powershell
  Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge\URLBlocklist'
  Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Google\Chrome\URLBlocklist'
  ```

- [ ] 关闭并重新打开 Edge 和 Chrome。黑名单中的可访问测试网站应被浏览器策略阻止；另一个未列出的测试网站仍可访问。
- [ ] 确认未被选中的第二台测试电脑仍可访问这两个网站。

若浏览器策略页显示错误、规则没有读回或测试网站本身不可访问，应记录为“未通过／无法判定”，不要把 DNS 故障当作黑名单生效。

## 5. 白名单：只允许列出的网站

在教师端把模式切换为“白名单：只允许所列网站”，填写至少一个可访问的允许域名，并把时长设为 45 分钟，再推送新策略。

- [ ] 学生机 Edge 和 Chrome 的策略页显示 URLBlocklist 含 `*`，URLAllowlist 含允许域名。
- [ ] 允许列表中的测试网站可访问；另一已确认可访问、但不在列表中的网站被浏览器策略阻止。
- [ ] 其他未选中电脑不受影响。
- [ ] 记录网站主域名与子域名的实际匹配结果；只按实测结果描述覆盖范围。

## 6. 一键解除、时长到期与重启恢复

教师端保持同一组目标电脑名／IP：

- [ ] 点击“一键解除所选电脑限制”。该按钮应自动签发停用策略，不要求先清空域名输入框或手动修改模式。
- [ ] 学生 Agent 返回成功后，Edge/Chrome 策略页和对应注册表 URLBlocklist／URLAllowlist 子项应被清除，两个测试网站恢复访问。
- [ ] 再次推送 45 分钟限制，记录教师界面显示的自动解除时间。到期前确认限制仍有效；到期后等待最多 2 分钟，确认只有本工具拥有且未被外部改动的浏览器规则被清除。
- [ ] 再推送 45 分钟限制，在限制到期前关闭学生机并等待超过到期时间，然后开机。Agent 启动时应检查过期策略并清除本工具规则；系统启动后两个测试网站恢复访问。
- [ ] 断开学生机与教师机网络后，确认教师端不会把无回执电脑显示为成功；本机策略仍按已签名的到期时间处理。
- [ ] 如果策略已过期但管理员／组策略改写了同名注册表值，确认 Agent 不删除外部值，并在 ProgramData 校区配置目录的 `agent-runtime.log` 写入 `policy-expiration-cleanup-failed`。日志不应出现学生浏览历史、网页内容、域名清单或密码。

Agent 每 30 秒检查一次到期策略，并在开机时立即检查。允许选择“不自动到期”；此时需由教师点击一键解除。学生机系统时间变化、休眠和策略刷新行为需要按学校环境记录。

## 7. 已有组策略冲突与失败恢复

只在可恢复的测试快照中执行。若学校已有 Edge/Chrome 组策略，不要修改真实域策略；可用本机测试策略模拟冲突：

```powershell
$path = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge\URLBlocklist'
New-Item -Path $path -Force | Out-Null
New-ItemProperty -Path $path -Name '1' -Value 'conflict-test.example' -PropertyType String -Force | Out-Null
```

- [ ] 推送限制时，Agent 拒绝覆盖现有策略；原有 `conflict-test.example` 值保持不变。
- [ ] 移除模拟值前先保存证据，或直接恢复快照；不要在生产机房运行这段命令。
- [ ] 记录学生离线、代理停止、错误校区公钥、重复／旧版本、外部改写策略时教师端显示的逐台状态和可恢复方法。
- [ ] 检查教师本机 `%LOCALAPPDATA%\VeyonCampus\WebsitePolicy\push-history.json`：最多保存最近 50 次推送及每台设备结果；不保存域名清单或签名内容。普通教师账号之外的 Windows 用户不应读取该文件。

## 8. 结果记录与恢复

每一项填写“通过／失败／未执行”，并保留不含凭据的截图、策略页和只读输出：

```text
测试项：
实际结果：通过 / 失败 / 未执行 / 无法判定
证据文件：
与预期的差异：
缺陷或需修改内容：
快照恢复结果：
```

测试结束后恢复 Windows 快照；不要在学生生产电脑上留下试验注册表值、测试校区密钥、代理任务或课堂限制。

## 9. 当前实现边界

- 常驻组件是独立 `VeyonCampus.Agent.exe` 和 SYSTEM 开机计划任务，不是 Windows Service。无参数双击 Agent 不显示教师／学生 GUI；服务存活、任务权限和普通用户无法停止必须由本清单实测。
- 学生便携 GUI 与教师控制台已经分为两个构建和发布包；清理通过后，学生清理清单中的 `VeyonCampus.StudentSetup.exe` 和便携 GUI 文件会被移除。教师签名私钥仍只应留在教师电脑的当前用户证书库。
- 网站限制只覆盖受支持的 Edge/Chrome 机器级 URL 策略；不覆盖 Firefox、便携浏览器、其他应用或任意网络流量。
- 本机自动检查和 Windows x64 发布成功不代表上述手工测试通过。所有系统级项目在收到实际证据前保持未验收。
