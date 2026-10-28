# Mac 课堂服务与恢复验证：2026-10-28

本记录覆盖本机代码、构建和隔离 HTTPS API；不代表校园 LAN、真实 Windows Agent 或配对手机已经验收。

## 本轮改动

- Mac 教师端创建共享课堂快捷管控服务，并向手机 HTTPS 服务注入读回/操作回调；恢复入口使用 `ITeacherNetworkDirectory`。
- Mac 目录提供校区和机房 ID。恢复按原范围匹配，拒绝同名错配、重复地点、缺失身份和已变化的主机绑定；Windows Veyon 无 ID 的目录保留名称匹配。
- 恢复任务只使用持久化状态记录的签名校区；原密钥不存在或上下文不一致时保留待同步项。
- InputGuard 教师身份在 Mac 读取并缓存 IOPlatformUUID；Windows 继续读取 MachineGuid。身份不可读、缺失、重复或格式错误时不发送键鼠命令。
- StudentSetup 构建从 Agent 的构建记录及已解析运行依赖收集载荷，保留目录结构，避免 `Core 3.dll` 等旧同步冲突副本被复制。

## 已验证

| 检查 | 结果与范围 |
| --- | --- |
| `--native-teacher-classroom-fixtures` | 通过。本机 Mac 身份读回稳定；拒绝异常 UUID 输出；跨校区同名机房不混用；主机绑定变化不重放旧恢复；两次服务重启沿用同一恢复 TaskId/Revision。目录故障由隔离适配器模拟，未向学生发送命令。 |
| `--mobile-control-api-fixtures` | 通过。真实本机 Kestrel HTTPS、配对及认证管线；快捷管控使用隔离回调，验证旧课堂 409、未授权 401、错误来源 403，以及未确认设备保持 Unknown/NeedsReview。画面使用合成 JPEG，不是真实学生桌面。 |
| `--classroom-quick-control-revision-fixtures` | 通过。旧 Apply 不能覆盖新 Restore 或课堂结束；截止时间校验、持久化退避及来源日志符合检查。 |
| `--mobile-channel-state-fixtures` | 通过。网站与键鼠状态独立，写入回执不当作浏览器效果确认，到期状态可辨识。 |
| TeacherConsole `osx-arm64` Release | 通过，0 警告、0 错误；另以现有试点开关开启的构建检查课堂管控代码。普通预览包继续沿用默认试点门控。 |
| TeacherConsole `win-x64` Release | 通过，0 警告、0 错误；现有课堂快捷管控试点开关开启，未运行 Windows 图形界面。 |
| StudentSetup `win-x64` Release | 通过，0 警告、0 错误。载荷包含 Agent 主程序、Core、runtimeconfig、deps 和壁纸；依赖清单引用的运行程序集齐全，Agent/Core 与本次构建摘要一致，无同步冲突副本。此检查未运行 Windows 安装或服务。 |
| Mac `.app` | 已重新生成 `artifacts/macos/Veyon Campus Teacher-1.1.40.app`。默认试点关闭，角色为 TeacherConsole，启动程序为 Mach-O arm64，helper 已打包，无 StudentSetup/Worker 载荷；最新默认包保留上一版为 `.previous.20261028010701`。Info.plist 标识和版本为 `com.veyon.campus.teacher` / `1.1.40`。未配置 Developer ID，产物仅供本机预览。 |
| Mac 试点 `.app` | 设置 `VEYONCAMPUS_CLASSROOM_QUICK_CONTROL_PILOT=true` 后由同一发布脚本生成 `artifacts/macos/Veyon Campus Teacher-1.1.40-Pilot.app`；显示名称标明课堂管控试点，架构为 ARM64，无 StudentSetup/Worker 载荷。脚本默认值仍为 `false`，没有替换普通预览包。未配置 Developer ID，试点包仅供本机测试。 |

追加检查：Mac 地点自动选择、按钮可用状态和应用操作现在都按校区/机房稳定 ID 判定，不再用组合显示名称反查。应用前会重新计算当前电脑地址并与读地点时的快照比较；地点 ID 缺失、目标列表过期或出现重复匹配时保持原课堂范围并显示错误。`--native-teacher-classroom-fixtures` 增加同名机房、地点改名、错误校区、缺失 ID 和地址变化检查；检查通过。TeacherConsole `osx-arm64` Release 重新 locked restore 后构建通过（0 警告、0 错误）。随后重新打包包含本次代码的 Mac `.app`，确认 ARM64 入口、1.1.40 元数据和教师角色载荷；未启动应用。

屏幕巡视准备状态：Docker helper 当前运行在本机，镜像标签为 `veyon-campus-helper:4.11.4`；容器内 `veyon-cli authkeys list` 返回空清单，因此没有对学生地址发起截图请求，也没有声称真实画面通过。首次设置页各项异步操作的进度指示统一改用 Flowery `DaisyProgress` 并补充可访问名称；Mac ARM64 TeacherConsole Release 编译和本机 `.app` 发布通过。最新预览包未启动，真实图形会话和学生桌面仍待现场验证。

为便于在本机继续验证课堂应用/恢复逻辑，发布脚本增加显式试点开关；`bash -n` 通过，带开关的完整 Mac 发布命令通过，生成独立 `-Pilot.app`，普通包的默认门控保持关闭。两个包均核对为 1.1.40 ARM64 教师端，且没有学生部署或 Worker 载荷。由于当前会话没有可用图形显示器，未启动其中任一应用。

2026-10-28 又尝试启动试点包进行桌面核验。系统硬件清单显示内建屏幕 Online，但 CoreGraphics 报告活动显示器数量为 0、主显示器 ID 为 1；前台启动得到 Avalonia.Native `RenderTimer` 错误 `-6661`。通过 System Events 确认应用窗口数为 0，进程无监听端口，随后结束该无窗口进程。Avalonia 上游问题 [#18895](https://github.com/AvaloniaUI/Avalonia/issues/18895) 和修复 PR [#21453](https://github.com/AvaloniaUI/Avalonia/pull/21453) 对应这一类 macOS 显示状态问题；PR 仍开放。项目当前使用 Avalonia 12.1.3。当前自动化会话不能承担真实桌面渲染验收；应用需要在 CoreGraphics 可枚举活动显示器的图形会话重新启动验证。

同次本机检查还发现默认 `TeacherCampusDirectoryStore` 文件尚不存在，helper 内 `veyon-cli authkeys list` 为空。因此当前没有本机保存的校区/机房目标，也没有可供 helper 使用的 Veyon 私钥；本轮没有对学生地址发出截图请求。需要在 Mac 首次设置中建立地点档案，并从安全渠道导入与原学生包匹配的私钥后，才能进行真实端到端截图验证。

检查程序集通过标准项目编译，输出放在 `/private/tmp/veyon-native-teacher-checks-20261028b`，以避免同步目录中未下载的 DLL 占位文件影响运行。验证过程中发现的 36 个未修改源码占位文件已从同一 Git blob 取回，写入前逐份校验 Git 摘要、索引、文件标识及修改时间；原有本地改动未被替换。

远端 `develop` 通过已连接 GitHub API 核实，仍为 `50508ece8188769c19f862b9215827650b4d9aa8`（1.1.40）。终端 GitHub DNS 失败，没有执行 pull 或 push。

## 仍需验收

真实 Mac 图形会话的 SukiUI/Flowery 页面、钥匙串授权与学生信任迁移；Mac 到 Windows Agent 的应用/恢复/截止/断网重连/重启；真实手机证书信任与后台切换；Veyon helper 的真实学生桌面及 10–70 台屏幕采集预算。整体目标继续保持进行中。

## 2026-10-28 活动图形会话追加复核

在当前可交互 macOS 图形会话中，使用 `cua_repl` 从当前源码重新发布并打开 `Veyon Campus Teacher-1.1.40-Pilot.app`。TeacherConsole 原生窗口正常显示；课堂、策略和设备页面可切换并可读取辅助功能树，未复现早先的 `RenderTimer -6661`。窗口截图尺寸为 2480×1820；当前应用没有课堂目录或学生目标，因此本次只核对了未配置状态的布局和禁用态。

设备页正确显示 helper 已运行但缺少当前校区教师私钥；学生状态和更新入口因没有目标/正式更新配置而保持禁用。没有导入密钥、建立课堂、连接学生机或发起截图请求。该结果补充了本机原生窗口实测，但不能替代不同窗口尺寸、键盘/屏幕阅读器、真实学生 Agent、配对手机和校园网络验收。试点包为本地未签名预览包，发布脚本保留了旧试点包备份。
