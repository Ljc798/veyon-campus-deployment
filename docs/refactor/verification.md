# 验证记录

## 基线

- Commit：`1a4cfb55afde1d9efe617ea528221e8c4275739c`；本地 `develop` 与 `origin/develop` 同步。
- `git fetch origin --prune`：完成。
- `experiment/skills-evaluation`：无本地分支；已知 UUID 实验 `e1f3d78` 与 UI 实验 `149d319` 都是 `origin/develop` 的祖先，不重复合并。
- 工作区原先只有任务书 untracked；执行过程中新增的 `docs/refactor/` 是本任务产物。未 stash/reset/clean。
- SDK 不在 PATH，但已定位 `/Users/alex/.dotnet/dotnet`：.NET SDK 10.0.401，macOS 27.2 arm64。
- `/Users/alex/.dotnet/dotnet build VeyonCampus.slnx -c Release`：PASS，0 errors、238 warnings。包含 NU1900 无法访问 NuGet vulnerability feed 的警告，以及已有 nullable / CA1416 跨平台警告；不能据此视为 Windows 构建验证。
- `/Users/alex/.dotnet/dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-build`：FAIL，前 23 项通过后在 `IssueThirdRoundChecks.Run()` 以 `system policy guidance remains platform-specific` 中止。断言只在非 Windows 分支执行；下一步需确认当前 macOS 产品提示和断言预期，不能把剩余默认 checks 计作通过。
- `... dotnet run --project tests/VeyonCampus.Checks/VeyonCampus.Checks.csproj -c Release --no-build -- --teacher-workflow-fixtures`：FAIL，前四条 workflow 检查通过，在 `TeacherWorkflowChecks.cs:57` 抛 `Shared target summary did not refresh after editing computers.`。该断言把摘要通知、选项数量与新加入目标是否默认勾选合并在一起；源码的 `WebsiteTargets` setter 会显式发出 `ClassroomTargetSummary` 通知，而新目标的选择状态沿用既有目标选择、没有既有值时为未选中。需拆分行为断言，先确认产品默认选择语义，再决定修代码还是修过期夹具；当前失败不能简单归因为 macOS。
- `npm run check`（`cloudfunctions/veyon-api`）：PASS，22 tests / 0 failures / 0 skipped；含本地 CloudBase contract double，不是真实云环境验收。

## Graphify 基线

执行 `graphify update . --force --no-cluster`：完成代码 AST 更新，13,867 nodes / 37,251 edges。报告解析缺口：25 个 SQL 无 `tree_sitter_sql`，58 个非代码/不支持扩展被跳过（包括 AXAML），`tests/VeyonCampus.Checks/Program.cs` 报 parser syntax error，14 个 PowerShell 文件无符号。不得将这份图谱描述为全源码/AXAML/SQL 完整图。

- 可复现入口：`docs/refactor/refresh-graph.sh`；完整 Graphify 输出位于本机 `graphify-out/graph.json`（该目录已忽略，不提交缓存），脚本再生成受限范围的 [device-flow-graph.json](device-flow-graph.json)（32 个关键节点 / 33 条边）。Mermaid 业务数据流在 [data-source-inventory.md](data-source-inventory.md)。

## Windows / 生产

未访问 Windows VM；未运行安装器、SCM、Veyon 真机或故障注入；未发布、未推标签、未部署 CloudBase、未向学生机推送。任务书要求这些在隔离 VM/最终人工测试完成后才能关闭 Gate。

## 2026-11-06 补充：构建恢复与 macOS 上传脚本

- `dotnet build VeyonCampus.slnx -c Release --no-restore -m:1`：PASS，0 errors（默认 StudentSetup 角色）。
- 按 CI 顺序先 `dotnet restore src/VeyonCampus.App/VeyonCampus.App.csproj --locked-mode -p:VeyonCampusRole=TeacherConsole`，再 `dotnet build ... --no-restore -p:VeyonCampusRole=TeacherConsole`：PASS，0 errors。此前该角色构建在 `TeacherViewModel.Classroom.cs` 因漏改的 `FindUniqueClassroomSeat` 6 参调用报 CS1501；已改为按 `DeviceLabel` 定位 session target 并调用 4 参版本。注意默认角色是 StudentSetup，它会把教师端文件从编译中移除，所以只看默认构建会得到假绿。
- `--classroom-session-fixtures`：PASS。
- 默认 Checks 仍在 `IssueThirdRoundChecks` 的“非 Windows 平台提示”断言处中止，与本次改动无关，未修复；CI 在 windows-latest 上运行，不受此影响。
- `scripts/upload-gitcode-release.sh`：`bash -n` 通过；`--help`、标签格式校验、`--verify-only`（正常/篡改两条路径）、`--dry-run`（gh 桩模拟全流程）、非交互缺 Token 保护均在本机 macOS 验证通过。真实 GitHub/GitCode 网络上传、Token 权限和 GitCode 侧读回未验证。
