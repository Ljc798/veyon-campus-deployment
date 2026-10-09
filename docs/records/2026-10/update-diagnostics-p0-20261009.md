# P0 更新诊断实现记录（2026-10-09）

## 目标与范围

按路线图 P0 实现双端更新结果/错误码规范与本机诊断。覆盖 Teacher 在线/离线更新、StudentSetup 在线/离线更新和教师发起的学生端局域网批量更新汇总。诊断留在当前 Windows 用户的 Local AppData；只有用户通过文件选择器主动导出时才生成 JSON，不调用 CloudBase 或网络接口。

## 实现

- Core 统一事件字段：模块、操作、结果、稳定错误码、当前/目标版本、严重性、可重试标记、UTC 时间和随机事件 ID。
- 分类器按异常类型分类，不读取异常消息或堆栈；双端更新界面改为显示错误码和安全的恢复建议。
- 线上/离线更新只确认更新助手启动时使用 UPDATE_HANDOFF_STARTED，不把助手启动误记为安装成功；批量学生更新按回执记录汇总结果。
- 本机存储最多保留 128 条，合计 512 KiB，单条上限 4 KiB；逐条原子写入，重解析点拒绝访问。
- 人工导出只包含白名单结构字段；学生批量更新仅写成功、需核对、失败计数，不写设备名、IP、Agent 指纹或逐台详情。
- Teacher 和 StudentSetup 各增加一个次要导出入口及本地隐私提示，复用原布局和主题。

## 自动化验证

- 环境：macOS arm64、.NET SDK 10.0.401。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-restore`：61/61 checks passed。
- TeacherConsole Release：`/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release --no-restore -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusIncludeInstaller=false`，0 警告、0 错误。
- StudentSetup Release：`/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj -c Release --no-restore -p:VeyonCampusRole=StudentSetup -p:VeyonCampusIncludeInstaller=false`，0 警告、0 错误。
- `git diff --check` 通过。提交号由本记录所在提交确定。

自动化验证不代表 Windows 实机安装、断网、UAC、安装回滚或校园网批量更新验收通过；本轮未生成安装包，也未运行 Windows 专属构建/安装测试。

## 未包含

无自动上传、云端故障汇总、诊断设置页、用户配置项或实机环境改动。路线图中后续的课堂会话模型仍为下一项独立 P0。
