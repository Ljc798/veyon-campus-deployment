# experiment/skills-evaluation — 变更清单、测试结果与合并建议

> 状态：**已完成，等待用户确认；未合并、未推送、未触碰生产环境**
> 实验分支：`experiment/skills-evaluation`　独立工作目录：`F:/github/veyon-campus-skills-evaluation`
> 基线：`origin/develop` @ `0f67214`（创建时的远端最新）　报告日期：2026-11-05

## 1. 执行边界核对

| 要求 | 实际 |
|---|---|
| 基于远端最新 `origin/develop` | ✅ `git fetch origin develop` 后以 `0f67214` 建分支/worktree，并二次核对 `ls-remote` 一致 |
| 独立 Git Worktree | ✅ `git worktree add -b experiment/skills-evaluation F:/github/veyon-campus-skills-evaluation origin/develop` |
| 不提交到 develop/main | ✅ 3 个提交全部在实验分支；`develop` / `main` 引用未变动 |
| 不修改生产环境 | ✅ 未改云函数、CloudBase、发布产物、注册表、服务、本机教师端档案；未运行会操作真实安装目录/防火墙/Agent 的夹具 |
| 分阶段提交 | ✅ 3 个独立提交（分析 → 核心重构 → UI），本报告与图谱刷新为第 4 个提交 |
| 使用四个已装 Skills | ✅ 见 `analysis.md` §1；graphify 图谱、security-audit guidance 审查、ponytail 范围裁剪、impeccable 设计上下文与评审 |
| 未经确认不合并 | ✅ 未执行任何 merge / rebase / push / cherry-pick |

> 主工作区 `F:/github/veyon-campus-deployment` 在本实验期间一直有**并发的未提交改动**（最后核对为 `TeacherViewModel.Classroom.cs`、`TeacherWindow.axaml.cs`、`VeyonNetworkObjectDirectory.cs`）。本实验没有读取、覆盖或 stash 它们；合并前需由用户决定如何处理。

## 2. 变更清单

总计 **9 个文件、+1106 / −8 行**（报告生成时统计；其中 930 行为分析文档与图谱数据，产品代码改动约 180 行）。

### 阶段 1 — `bdb3af8` 分析产物（新增，零产品代码）

| 文件 | 内容 |
|---|---|
| `docs/experiments/skills-evaluation/analysis.md` | 四个 Skills 的用法与模式、Design Context、知识图谱 Mermaid、3 个架构根因、6 条安全聚焦发现、UI 评审、范围裁剪与验证策略 |
| `docs/experiments/skills-evaluation/build-graph.py` | graphify 风格静态图谱构建器：把节点锚定到源码符号并回查行号，符号消失会进入 `unverified_nodes` |
| `docs/experiments/skills-evaluation/location-sync-graph.json` | 32 节点 / 42 边的地点与设备目录同步链路图谱，`unverified: none` |

### 阶段 2 — `e1f3d78` 地点身份架构修复（核心）

| 文件 | 改动 |
|---|---|
| `src/VeyonCampus.Core/VeyonNetworkLocation.cs` | 增加 `Guid? ObjectId { get; init; }`（+5 行）。**不改构造签名**，4 处现有构造点零改动；不参与任何持久化 |
| `src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs` | 新增纯函数 `BuildLocationsById`（按 UUID 分组、同名不合并、保留 0 台电脑地点、无 UUID 回退按名称）、`SelectLocationObject`（写回 UUID 优先，歧义返回 null）、私有 `ProjectLocation` 与两个类型判定 helper（+94 行）。**原有 `ParseLocationsFromObjects` / `ParseLocationsFromSnapshot` / `ParseLocationsExport` 一行未改**，指纹比对与名单显示保持原语义 |
| `src/VeyonCampus.App/TeacherViewModel.cs` | ① `TryBindRoomComputersToSeatGridAsync`：用写回回执的 `ParentObjectId` 经 `SelectLocationObject` 选地点（R21 防同名漂移），无 UUID 时回退原按名逻辑；② `RefreshVeyonDeviceDirectorySummaryAsync`：在 `Reconcile` 写盘**之前**用 UUID 投影做同名不同身份的只读检测与阻断（S-01 / R19 / R20） |
| `tests/VeyonCampus.Checks/Program.cs` | 新增 `--veyon-location-identity-fixtures` 开关、`CheckLocationIdentityProjection()` 断言（R19/R20/R21、空地点、名称回退、旧投影不变），并纳入默认全量套件 |
| `location-sync-graph.json` | 增量重跑：两个新增节点从 `unverified` 变为已验证 |

### 阶段 3 — `149d319` UI 优化（零功能改动）

| 文件 | 改动 |
|---|---|
| `src/VeyonCampus.App/TeacherWindow.axaml.cs` | 「确认交换座位」100+ 字技术解释压缩为一句，保留唯一需用户判断的约束（U-01） |
| `src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml` | 两个「重试」按钮补 `ToolTip.Tip`，说明只读 / 会写 Veyon 的差别（U-02）。不改布局、绑定、事件、可见性条件 |

### 阶段 4 — 本提交

`REPORT.md`（本文件）与图谱 `source_ref` 刷新。

## 3. 构建与测试结果

使用 64 位 SDK `C:/Program Files/dotnet/dotnet.exe`（10.0.401；PATH 上的 32 位 `dotnet` 无 SDK）。

| 验证项 | 命令 / 开关 | 结果 |
|---|---|---|
| Checks 项目 Release 构建 | `dotnet build tests/VeyonCampus.Checks/... -c Release` | ✅ 0 错误（113 既存警告：CA1416 / CS8603） |
| App 项目 Release 构建（含 AXAML 编译） | `dotnet build src/VeyonCampus.App/... -c Release` | ✅ 0 错误（102 既存警告） |
| **完整解决方案 Release 构建** | `dotnet build VeyonCampus.slnx -c Release` | ✅ **0 错误**（233 既存警告，均为未改动文件的平台兼容性警告） |
| **Checks 默认全量断言套件** | 无参数运行 `VeyonCampus.Checks.dll` | ✅ **exit 0，`All 90 checks passed.`**（含本轮新增的 R19/R20/R21 Check） |
| 新增夹具 | `--veyon-location-identity-fixtures` | ✅ PASS |
| 既有相关夹具（9 项） | dump 解析、installer trust、teacher restart、native classroom、room/health selection、quick-control revision、student agent channel、student application update | ✅ 全部 PASS |
| `--teacher-workflow-fixtures` | — | ❌ exit 127，`Shared target summary did not refresh after editing computers.`（`TeacherWorkflowChecks.cs:57`） |
| **11 个独立开关合计** | — | **10 PASS / 1 既存失败（下条对照证明）** |
| **基线对照复现** | 把 `0f67214` 导出到临时目录、独立构建后跑同一开关 | ✅ **同样 exit 127、同一异常、同一行号** → 确认为**基线既存失败，非本轮引入** |

未运行的夹具：`--agent-*`、`--website-policy-registry-*`、`--student-update-atomic-storage-*`、`--process-fixture` 等会操作真实注册表 / 服务 / 安装目录 / 子进程的开关，按「不修改生产环境」要求主动跳过；`--veyon-directory-read-fixtures` 需要外部 `config export` 文件，本机未提供。

## 4. 兼容性结论

- **数据兼容**：三个持久化 store（`TeacherCampusDirectoryStore` schema 2、`ClassroomSeatGridStore` schema 5、`VeyonDeviceDirectoryStore` schema 1）及其 `JsonSerializerOptions` **一个文件都没改**。新增的 `ObjectId` 是内存投影类型的 `init` 属性，不进入 `campus-directory.json` / `classroom-seat-grid.json` / `veyon-device-directory.json`；这些 store 用 `UnmappedMemberHandling.Disallow`，因此**不新增落盘字段**是硬约束（见 `analysis.md` S-06）。
- **接口兼容**：没有删除或修改任何公开方法签名；只新增两个 `public static` 纯函数与一个可空 `init` 属性。`VeyonNetworkLocation` 的四个现有构造点、所有 `with` 表达式、`ITeacherNetworkDirectory.ReadLocations()` 均不变。手机端 `TeacherMobileControlServer` 只读取 `Targets` / `TargetDisplayNames` 并投影成 DTO，不序列化该 record，API 响应形状不变。
- **功能兼容**：`ParseLocationsFromObjects` / `ParseLocationsExport` / `BuildImportPreview` / `TeacherCampusDirectoryStore` 的既有断言全部继续通过（默认全量 90 项全绿）；CLI 调用协议、提权 Worker 协议、座位图事件与绑定名均未改动。
- **唯一的行为变化**：当 `networkobjects dump` 中出现**同名但不同 UUID** 的地点时，`RefreshVeyonDeviceDirectorySummaryAsync` 现在会在写 `veyon-device-directory.json` 之前阻断并显示既有错误文案，而不是把两份数据按名称合并后继续。这是 S-01 的修复意图（v2.2 §B「只检测与阻断」），但属于可观察的行为差异；现场若确实存在重复地点，教师端会提示先人工核对而非静默使用。

## 5. 尚未验证 / 明确未做

- 未在 Windows + Veyon 4.11.2 实机验证：重复地点阻断的真实 UI 表现、注册表快照回退与新 UUID 投影同时出现时的边界（S-04）、删除链路的不可逆顺序（S-02）。
- 未做：schema 2→3 与 `TeacherRoomProfile` 地点 UUID 落盘、`AddLocation` 返回 UUID 持久化、15 处按名查找全量替换、`config import` 自动去重与回滚、跨文件操作 journal、多教师机共享写锁。这些都涉及数据迁移或整份配置覆盖写，需用户确认并另开实验。
- 未提供 UI 截图（本环境无桌面会话）；AXAML 已通过 Release 编译。
- 未验证手机端与多教师机并发场景。

## 6. 合并建议（**等待确认，不执行**）

实验期间 `origin/develop` 持续有并发提交（至少 `87d37ce Record the v1.1.58 registry fallback release`，随后还有更新），这些提交主要涉及 `.workbuddy/memory/`、`docs/maintenance/` 等文档，**与本实验 9 个产品代码/文档改动文件零重叠**，因此代码层面预期无冲突。

建议顺序（**任何一步都需用户明确确认后才会执行**）：

1. 用户先审阅 `analysis.md` 与本报告，特别是「唯一的行为变化」是否满足现场预期；确认主工作区那 3 个并发未提交改动的处置方式。
2. 在隔离 Windows 教师机（Veyon 4.11.2）上运行一次真实目录读取 + 建档 + 绑定 + 删除回归，确认重复地点阻断不会在正常单地点场景误触发。
3. 确认后再把实验分支更新到最新 `develop`（`git rebase origin/develop` 或 merge），重跑完整 Release 构建与默认全量套件。
4. 由用户决定合并方式：建议 **squash 合并**或保留 3 个阶段提交（便于逐阶段回滚）；合并目标只应是 `develop`，不得直接进 `main`。
5. 只有收到明确「可以合并」的指令后才执行合并，且不做 push、不触发发布流水线。

> 当前分支状态：实验分支相对 `origin/develop` 为 ahead（4 个实验提交及以上）、behind 少量并发提交；`develop` 在本实验期间仍在前进，具体数字与差异请在合并前用 `git fetch origin develop` + `git status -sb` 重新核对。未设置上游到 develop 之外的分支，未配置自动推送。
