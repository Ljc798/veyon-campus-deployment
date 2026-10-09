# Student Companion 登录会话入口 S1-02 实现记录

日期：2026-10-10
环境：macOS arm64，.NET SDK 10.0.401。
范围：Student Companion 独立登录会话应用、StudentSetup 发布集成与默认登录启动。

## 实现结果

- 新增独立 `VeyonCampus.Companion` Avalonia 项目，使用 `asInvoker` manifest、当前登录用户身份和 Windows 登录会话单实例互斥。托盘启动参数 `--startup` 会隐藏窗口；托盘单击可打开窗口，关闭窗口收起到托盘，菜单保留打开与退出。
- StudentSetup 的 Windows x64 自包含发布目录新增 `StudentCompanion/` 子目录。Student 安装器默认创建开始菜单入口及 `{commonstartup}` 启动快捷方式，不增加启动选项；TeacherConsole 发布目标不运行 Companion 发布目标。
- 发布脚本先独立锁定还原 Companion 的 win-x64 依赖，再检查学生包中的 Companion EXE/DLL、与 StudentSetup 一致的版本以及 `asInvoker` 清单；教师包若包含 Companion 目录或程序集则拒绝继续。Companion 自己不引用 Worker 或 Student Agent，也不开放网络端口、不访问 CloudBase。
- Windows CI 安装器 smoke 增加验证：学生包安装后公共启动目录快捷方式必须指向 Companion 并带 `--startup`，Teacher 包不应有 Companion；卸载后 Companion 文件和快捷方式都必须消失。
- 状态卡默认显示“等待教师端连接”。连接和活动课堂快照有确定的文案及输入校验；缺少教师数据源时不展示虚构课堂信息。
- 为减少学生端选择和重复操作，登录启动是固定默认行为，状态卡只保留关闭即收起这一种窗口操作；不增加配置页、服务器地址或隐藏按钮。

## 主要文件

- 应用与状态模型：[VeyonCampus.Companion](../../../src/VeyonCampus.Companion/VeyonCampus.Companion.csproj)、[StudentCompanionViewModel.cs](../../../src/VeyonCampus.Companion/StudentCompanionViewModel.cs)、[StudentCompanionWindow.axaml](../../../src/VeyonCampus.Companion/StudentCompanionWindow.axaml)
- StudentSetup 发布目标：[VeyonCampus.App.csproj](../../../src/VeyonCampus.App/VeyonCampus.App.csproj)
- 安装器登录启动入口：[VeyonCampus-Student.iss](../../../installer/VeyonCampus-Student.iss)
- 包角色检查：[package-windows-offline.ps1](../../../scripts/package-windows-offline.ps1)
- 需求/设计/任务：[requirements](../../../specs/student-companion/requirements.md) · [design](../../../specs/student-companion/design.md) · [tasks](../../../specs/student-companion/tasks.md)
- 自动检查：[StudentCompanionChecks.cs](../../../tests/VeyonCampus.Checks/StudentCompanionChecks.cs)

## 验证

- `dotnet build VeyonCampus.slnx -c Release --no-restore -m:1`：通过，0 警告、0 错误。
- `dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-build --no-restore`：63/63 通过，新增默认断开、已连接无课堂、活动课堂、目标数量边界和非法状态拒绝检查。
- TeacherConsole、StudentSetup 分别执行锁定还原和 Release 构建：均通过，0 警告、0 错误。
- TeacherConsole 与 StudentSetup 均完成 Windows x64 自包含交叉发布。检查 StudentSetup 输出含 `StudentCompanion/VeyonCampus.StudentCompanion.exe`，TeacherConsole 输出没有 Companion 文件；Companion 可执行文件识别为 Windows x64 GUI PE。
- 用临时测试公钥再次交叉发布 StudentSetup，并用资源验证器确认包内四份 Core 程序集均固定该公钥；同时确认 Companion 的 Core 不重复嵌入 Veyon 安装器。测试公钥只在临时目录，不是生产发行密钥。
- `git diff --check`：通过。

本机不是 Windows，未运行 Inno Setup 编译器；所以本记录不表示学生安装器 EXE 已重新生成，也不表示 Windows 登录启动、系统托盘实际交互、升级覆盖或卸载已经实测。应在 Windows x64 上运行 `scripts/check-app.ps1 -WindowsPackageSmoke` 后，再按实机验收清单验证这些行为。

## 后续边界

此里程碑只完成 S1-02 的独立 Companion 外壳和安装集成。Teacher 到 Student 的认证 LAN 状态通道与课堂实时数据源属于 S1-03，当前界面保持未连接状态；Windows 实机验收对应本规格任务 5。
