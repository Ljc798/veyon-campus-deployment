# 学生确认解决的求助闭环

日期：2026-10-10（Asia/Hong_Kong）

## 用户体验与默认选择

学生仍只看到一个课堂操作按钮。发出求助后按钮显示发送状态；等待教师时显示求助状态；收到教师回复后，同一位置的按钮变成“标记已解决”；解决后按钮恢复为“需要老师帮助”。求助原因、设备、课堂和网络由当前已签名会话自动确定。没有新增设置页、原因菜单或第二个解决按钮。

教师回复后桌面和手机显示“等待学生确认”；学生确认后，两个界面把原求助更新为“已解决”，不会新增一条求助。

## 实现路径

1. Student Companion 的 `StudentCompanionViewModel` 保留原求助 ID，并只在收到与该 ID 对应的教师回复后开放解决动作。
2. Companion 将求助 ID 通过独立的 loopback 端点交给 Student Agent。Agent 从当前课堂授权中取得 session、目标和教师地址，生成并签名 `HelpResolved`，不接受 Companion 指定事件类型或身份。
3. Teacher API 再次验证活动 session、目标、固定 Agent 身份、原始学生求助以及已发送的教师回复。重复解决按原求助 ID 去重；未回复、错 session 或错目标均拒绝。
4. Teacher 桌面将事件关联到原求助行；手机网页从同一课堂事件流读取并更新原求助卡片。

事件仍只在教师进程内存队列中短期暂存，不新增端口、不经过 CloudBase，也不保存聊天历史。断网时原求助 ID 与待确认状态保留；恢复连接后可重试确认。

## 变更范围

- `ClassroomEventTransport` 和 Agent loopback 端点：签发、转发并验证学生解决确认。
- `StudentCompanionEventPoller`、`StudentCompanionViewModel` 和 Companion 窗口：单按钮状态闭环及离线重试状态。
- Teacher API、桌面 ViewModel 和手机课堂事件页：校验解决条件并同步更新原求助状态。
- `specs/classroom-event-channel/`：补充用户需求、信任边界、验收任务；第 8 项代码与自动检查完成，第 9 项现场验收保持未完成。
- 手机控制指南、架构概览和文档索引：同步当前行为与验证边界。

## 验证

- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx --configuration Release --no-restore -m:1`：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet build src/VeyonCampus.App/VeyonCampus.App.csproj --configuration Release --no-restore -p:VeyonCampusRole=TeacherConsole -m:1`：0 警告、0 错误。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj --configuration Release --no-build --no-restore`：完整检查 **65/65** 通过，覆盖单按钮状态、回复关联、提前到达的回复、断线重连、未回复时拒绝、重复解决去重和手机事件流。
- `node --check src/VeyonCampus.App/MobileWeb/app.js`、`node --check src/VeyonCampus.App/MobileWeb/service-worker.js` 与 `git diff --check`：通过。
- 临时 Playwright + 本机 Chrome 以 390×844 手机视口打开手机控制页和本地合成 API：先观察“等待学生确认”，下一轮事件后更新为“已解决”及“学生已确认解决。”；页面错误 **0**。截图只保存在 `/tmp`，不进入仓库。

浏览器检查使用本地合成数据，不覆盖真实 HTTPS、手机证书信任、Windows HTTP.sys/SYSTEM 或校园 LAN 行为；这些仍需现场验收。

## 尚待现场验收

在 Windows 教师机、学生机和已配对手机上验证 Teacher 回复后学生按钮转换、学生确认后桌面与手机同步变为“已解决”，以及断网恢复、下课撤销和手机浏览器实际表现。完整范围见[课堂事件通道任务 9](../../../specs/classroom-event-channel/tasks.md)。
