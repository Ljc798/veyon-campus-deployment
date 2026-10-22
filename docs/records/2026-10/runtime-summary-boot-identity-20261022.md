# 设备重启识别与 BootId 签名摘要

日期：2026-10-22  
版本：TeacherConsole / StudentSetup 1.1.25、Student Agent 0.4.55、Core 0.4.53、InputGuard 1.1.25  
状态：Release 编译和本地安装器构建完成；学生机重启识别、输入恢复和无误报行为待用户统一现场验收。

## 实现

- 在已签名的轻量运行摘要中加入可选 `BootId` 字段。旧 Agent 缺少该字段时，教师端继续验证并显示其余摘要，不把字段缺失解释成设备重启。
- 学生 Agent 通过 Windows 系统 UTC 与 `Environment.TickCount64` 估算启动时间，结合本机名生成 SHA-256 标识并在 Agent 进程内缓存。同一 Windows 启动期间，单独重启 Agent 不应改变标识。
- 教师健康页按目标保留已观测标识；收到新签名摘要且标识变化时创建待知悉的“学生电脑已重启”事件。若上次新鲜摘要包含尚有效的 InputGuard 锁定租约，提醒重启后不会自动恢复锁定。
- 重启检测只生成提示，不发送 Agent 命令、不重新锁定设备。提醒由教师知悉后从当前异常列表移除。
- BootId 是摘要内的本机匿名 SHA-256 值，不显示给教师，也不会上传到 CloudBase。该标识依赖 UTC 与运行时间估算；异常校时可能造成变化，现场需要检查 Agent 重启、Windows 重启和时间同步的差异。

## 本地构建

安装器都位于 `artifacts/test-build-phase2-boot-identity-20261022/`：

| 安装器 | 大小 | SHA-256 |
| --- | ---: | --- |
| [StudentSetup 1.1.25 Boot Identity 测试包](../../../artifacts/test-build-phase2-boot-identity-20261022/VeyonCampus-StudentSetup-1.1.25-Boot-Identity-Test.exe) | 67,192,077 bytes | `410A377CA53CA3A155B4E3722E60E1E3E248DBD6D0B01541FE2E3A020A70AB65` |
| [TeacherConsole 1.1.25 Boot Identity 测试包](../../../artifacts/test-build-phase2-boot-identity-20261022/VeyonCampus-TeacherConsole-1.1.25-Boot-Identity-Test.exe) | 69,335,346 bytes | `5D88B49C0CA67493F4F315FAF7CFA3D0443CB387EE2ECC9E15FC4AAFEB464700` |

TeacherConsole 包启用了既有课堂快捷管控试点标记。脚本已核对包内 Veyon 资源、角色构建和更新助手；未配置 Developer Release PEM 公钥，因此应用内更新安全停用。学生端安装器包含 Agent 0.4.55 和 InputGuard 1.1.25。

Release 编译成功，0 个错误。打包过程保留既有 Companion XAML runtime-loader 警告和 TeacherWindow 的 2 条 `TextBox.Watermark` 过时 API 警告。未运行测试套件、启动或安装程序、未做学生机或手机实测，也没有上传 GitHub、GitCode 或 CloudBase。

## 用户现场验收

1. 安装 TeacherConsole 1.1.25；选择一台可恢复学生机，用 StudentSetup 1.1.25 更新 Agent。
2. 打开设备健康页，确认该设备显示映射的学生姓名。等待摘要建立基线后，只重启 Student Agent 服务；确认不会出现 Windows 重启提醒。
3. 在有有效键鼠锁定租约时重启 Windows。设备重新签名响应后，应出现待知悉“学生电脑已重启”异常，并说明旧锁定不会自动恢复；确认设备没有自动重新锁定。
4. 知悉提醒后，异常从当前列表移除。重复 Agent 重启和正常时间同步，确认不会重复产生重启事件。
5. 用较旧 Agent 验证缺少 `BootId` 只代表该字段不受支持；其他签名状态仍可读取，界面不显示重启异常。
6. 如果校时导致误报，记录校时前后的 UTC 时间、Agent 服务状态和 Teacher 健康卡片内容。

详细阶段验收步骤见[统一现场验收清单](../../下一阶段统一现场验收清单.md)和[Phase 2 实施记录](../../../specs/next-phase/phase-2-runtime-health.md)。
