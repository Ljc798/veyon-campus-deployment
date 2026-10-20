# Veyon Campus 安全增量更新系统设计

> 目标：以最小下载量、最少人工操作，安全地将一个校区中**不同版本**的教师端与学生端更新到目标版本；支持纯净 Windows 离线部署、跨版本升级、失败回滚以及局域网分发。
>
> 文档性质：面向 Codex 和项目维护者的架构设计、实施顺序、当前实现记录与验收基线。文中明确区分“源码已实现”“构建已验证”“Windows 实机已验收”和“尚未开发”。
>
> 基线：2026-10-20 `develop`。教师端推送的 StudentSetup + SYSTEM Agent 原子事务、事务终态签名读回和旧 Agent 能力门禁已实现并通过 macOS 两种角色构建；Windows 10/11 故障注入和真实设备验收仍未完成。签名文件清单、文件对象缓存和文件级增量安装协议仍未开发，且不得先于原子事务 Windows 验收启用。

## 1. 核心结论

- **源码只在 CI / 开发机构建。** 终端不下载 `.cs` 源码，也不运行 `dotnet build`；它接收已经编译完成的 EXE、DLL、资源和配置模板。
- **每个 Release 发布完整的目标文件清单**（路径、大小、SHA-256、所属组件、依赖及可删除属性）。设备将本机实际文件与目标清单比较，直接跨多个版本升级，无须逐版本替换文件。
- **GitCode / GitHub Release 存储文件对象及完整安装器；CloudBase 只提供版本索引与元数据。** TeacherConsole 统一下载、校验、缓存，再通过校区局域网分发。
- **优先实现文件级增量，再考虑二进制差分。** 两者最终都必须重建出经校验的**完整目标文件**。
- **更新安全性高于下载体积。** 签名验证、受保护暂存、事务式切换、健康检查、自动回滚和旧协议兼容是正式发布门槛。
- **程序更新≠热更新。** 替换已编译文件后，相关进程通常要重启；网站策略、课堂消息和其他配置应继续走现有实时通信机制。

## 2. 当前项目特点与边界

| 当前架构 | 更新设计的影响 |
|---|---|
| TeacherConsole、StudentSetup、SYSTEM Agent、StudentCompanion、EdgePolicyExecutor、Worker、UpdateHelper 等独立进程 | 不能假设只替换一个 DLL 就完成整个产品更新；组件依赖和进程生命周期要协调 |
| Windows x64 使用机器级共享 .NET 10 Runtime | 多数业务版本只更新应用文件；涉及运行时版本变化时必须先完成前置条件检测与受控安装，运行库不应随小更新反复打包 |
| 教师机与学生机在同一校区局域网，学生数量不一 | 教师机适合作为下载缓存和分发节点，学生机不必分别访问境外或公网镜像 |
| 现有完整安装包下载/覆盖安装流程 | 必须保留作为**首次安装、老版本兼容迁移、重大结构变更和灾难恢复**通道 |
| Veyon 4.11.2 官方安装器及既有密钥/校区配置 | 不能因目标文件清单缺少某文件就删除 Veyon 安装、认证密钥、配置、日志或用户数据 |

### 2.1 当前源码实现与待开发边界

**已在源码中实现的完整安装器原子更新闭环：** TeacherConsole 读取每台设备的已签名版本状态，下载并校验完整 StudentSetup 安装器，通过临时局域网端点分发；SYSTEM Student Agent 验证教师签名命令和开发者发行签名，在受保护目录记录联合事务，保留旧 StudentSetup，安装目标版本，暂存版本隔离的 Agent，并由独立 Student UpdateHelper 切换任务、启动新 Agent、核验版本与配置指纹。健康通过才提交；失败则恢复旧 StudentSetup 和旧 Agent，回滚或恢复状态会保留在 journal 中。

2026-10-20 补齐的终态协商基于状态协议 v4：Student Agent 报告 `StudentUpdateProtocolVersion`，教师端用本次 `commandId` 查询签名事务状态；状态包含 `inProgress`、`committed`、`rolledBack`、`needsReview` 或 `notFound`，并绑定更新前、目标版本。教师端必须同时验证校区、随机数、签名、固定 Agent 身份和实际版本读回。旧状态协议仍可用于只读版本盘点，但不具备 v4 事务能力的设备不会收到远程更新命令。

对应源码入口：`src/VeyonCampus.App/TeacherViewModel.cs`、`src/VeyonCampus.Core/TeacherMobileStatusProtocol.cs`、`src/VeyonCampus.Core/StudentApplicationUpdates.cs`、`src/VeyonCampus.Core/StudentUpdateAtomicTransaction.cs`、`src/VeyonCampus.Core/StudentApplicationUpdateHandoff.cs` 与 `src/VeyonCampus.Core/WebsitePolicyRuntime.cs`。这只说明源码和 macOS 构建通过，不代表 Windows SYSTEM ACL、计划任务、重启及失败回滚已验收。

**尚未开发：** 完整目标文件清单与发布签名、只读安装差异计划、按哈希去重的教师端缓存、学生端文件对象安装协议、文件级增量应用和二进制差分。当前网络分发内容仍是完整 StudentSetup EXE。

## 3. 最终更新架构

```text
开发者修改 .cs / .axaml 等源码
             │
             ▼
      GitHub Actions (Windows)
     构建 Teacher / Student 全套产物
     生成完整目标文件清单 + 组件依赖
     计算 SHA-256 + 签名发布元数据
     可选生成二进制差分补丁
             │
      ┌──────┴────────┐
      ▼               ▼
GitCode Releases  GitHub Releases
   国内优先源         备用镜像
      └──────┬────────┘
             ▼
  CloudBase：版本索引 / 渠道 / 下载地址
             │
             ▼
 TeacherConsole：查询目标版本，汇总校区机器差异
             │
             ▼
  校区可信内容缓存 (按 SHA-256 去重)
             │  局域网 HTTP，限速、续传、校验
       ┌─────┼─────┐
       ▼     ▼     ▼
     PC-01 PC-02 PC-03 ... 不同原始版本
       │     │     │
       └─────┼─────┘
             ▼
  Student Agent：受控命令、目标版本检查
             ▼
  UpdateHelper：下载/暂存/验签/迁移/切换
             ▼
     重启相关进程 + 健康检查
             │
       成功确认 / 失败回滚
```

**信任边界：** GitCode、GitHub、CloudBase、教师机缓存均视为**内容传输来源**，不是代码可信性的最终依据。学生端必须用预置的开发者公钥验证目标更新元数据签名，然后验证各文件哈希；教师端指令仍遵循现有的校区级认证/授权协议。**源码下载链接不属于更新来源；终端不得编译源码或执行未在签名发布中授权的脚本。**

## 4. 完整目标文件清单（关键设计）

**不建议只发布“本版本相较上版本的变化文件”作为唯一依据。** 推荐每个正式版本保存所有受更新器管理的目标文件，学生机根据实际哈希判断：

| 本机实际状态 vs 目标清单 | 操作 |
|---|---|
| 路径存在且 SHA-256 一致 | 复用，不下载 |
| 路径存在但 SHA-256 不同 | 下载目标文件或适用差分补丁 |
| 目标需要、设备没有 | 下载并新增 |
| 本机属于**旧版受管理清单**、目标版本已废弃 | 经删除规则验证后清理 |
| 未列入受管理范围的配置、密钥、日志、用户文件 | 保留，不参与自动删除 |

签名清单示意（字段需在正式协议设计时定稿；此处哈希为占位符）：

实际字段、角色路径和交叉校验规则以 [file-manifest-v1.schema.json](../specs/cloud-updates-and-installers/file-manifest-v1.schema.json) 与 [file-manifest-v1.md](../specs/cloud-updates-and-installers/file-manifest-v1.md) 为准。举例：StudentSetup 使用 `product=VeyonCampus.StudentSetup`、`role=StudentSetup`、`architecture=win-x64`；组件文件按固定 `rootId` 分类，不在清单里携带绝对目标路径。

清单示例（省略非关键文件）：

```json
{
  "schemaVersion": 1,
  "product": "VeyonCampus.StudentSetup",
  "role": "StudentSetup",
  "architecture": "win-x64",
  "version": "1.2.3",
  "channel": "stable",
  "releaseSequence": 123,
  "createdUtc": "2026-10-20T00:00:00Z",
  "expiresUtc": "2026-11-20T00:00:00Z",
  "minimumUpdaterVersion": "1.2.0",
  "requiredFileUpdateProtocolVersion": 1,
  "roots": [
    {"id": "StudentApp", "location": "ProgramFiles/Veyon Campus/Student", "activation": "transactional-root"},
    {"id": "StudentAgentVersioned", "location": "ProgramData/VeyonCampus/WebsitePolicyAgent/{agentVersion}", "activation": "versioned-root"},
    {"id": "StudentUpdateHelperVersioned", "location": "ProgramFiles/Veyon Campus/Updater/Student/{version}", "activation": "versioned-root"}
  ],
  "components": [
    {
      "id": "StudentSetup",
      "rootId": "StudentApp",
      "version": "1.2.3",
      "dependencies": [],
      "files": [
        {"path": "VeyonCampus.StudentSetup.exe", "sizeBytes": 1234567, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
      ]
    }
  ],
  "requiredRuntimes": [
    {"framework": "Microsoft.NETCore.App", "version": "10.0.13", "architecture": "x64"}
  ],
  "migrationCatalog": [],
  "directSources": [
    {
      "version": "1.2.2",
      "manifestSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "requiredMigrations": [],
      "managedDeletes": []
    }
  ]
}
```

实际发布时清单本体与 detached 签名分开（`manifest.json` + `manifest.sig`）。签名封装包含算法、密钥 ID 和 Base64 签名；签名绑定域分隔符及 `manifest.json` 的**原始 UTF-8 字节**，客户端验签前不得重新序列化。发布端输出无 BOM 的确定性 JSON。密钥轮换、防回滚、过期和删除规则见上述协议文件。

**注意：** SHA-256 是哈希，不是数字签名；不能只凭文件哈希宣称下载来源可信。清单必须是**完整目标状态**，而非只有上版本变更；目标对象 URL 应由受信任元数据给出，并绑定对象哈希、大小与允许的下载源。

**可重复构建不是跨版本差异计算的前提：** 无论同一份源码能否生成逐字节一致的产物，发布时都只认实际构建并签名的目标文件字节；设备只比较已安装文件与目标文件的真实 SHA-256。

## 5. 多版本设备如何共同更新

示例：同一校区有 PC-01 (v1.1.7)、PC-02 (v1.2.0)、PC-03 (v1.2.2)，目标 v1.2.3。

1. TeacherConsole 获取 v1.2.3 的**同一份**签名目标清单。
2. 各学生机检查本地实际文件状态，返回需要的**内容哈希集合**与兼容性/迁移需求。
3. 教师机对全校区需求做**集合并集**，以 SHA-256 为键去重，计算缺少的对象。
4. 仅从 GitCode（失败时 GitHub）下载未缓存的对象，并完整校验；回退镜像不改变文件哈希或签名要求。
5. 通过局域网把各机需要的对象分发出去；学生机独立校验、安装和确认。

**不需要**依次比较 v1.1.8、v1.1.9、v1.2.0……的文件差异；但如果中间版本存在必须执行的数据库/系统迁移，迁移可能需要按规定顺序执行。对不同版本机器**分别生成安装计划**；教师机只合并内容下载需求，绝不将同一份未经兼容性检查的“覆盖脚本”直接广播到全教室。

## 6. 下载什么：GitCode 不是源码仓库式更新

**更新对象是 CI 预构建产物，不是 Git 提交、源码 ZIP，也不是运行时编译的 C# 文件。**

建议保留以下 Release 附件（示意）：

```text
Release v1.2.3/
  VeyonCampus-Student-Setup-1.2.3-win-x64.exe   # 完整离线安装器，保底
  VeyonCampus-Teacher-Setup-1.2.3-win-x64.exe
  student-manifest.json
  student-manifest.sig
  teacher-manifest.json
  teacher-manifest.sig
  objects/sha256-<hash>.blob                  # 可按对象存储或分包
  ...
```

**部署演进：**

- **阶段一：按组件 ZIP 发布**，如 `agent.zip`、`companion.zip`、`executor.zip`；每个组件内部仍应列出完整文件哈希和兼容依赖。实现快，但可能多下载未变化文件。
- **阶段二：按内容哈希发布不可变文件对象**，跨角色、版本缓存复用；文件数过多时可采用不可变 pack 和对象索引，避免 GitCode Release 附件数量、单文件大小及速率限制。
- **阶段三：按需要增加差分补丁对象**，不是替代完整目标文件兜底下载。

GitCode 具体附件数量、单文件大小、直链有效性、鉴权和带宽限制，须以当前服务实测及发布 API 能力为准，不能假定无限制。

## 7. 更新方式的正确分层

### 7.1 文件级增量更新（第一版必须完成）

本机 DLL/EXE 哈希与目标相同 → 不下载；不同 → 下载完整的**目标编译文件**，经验证后安装。**学生机不用构建。**

### 7.2 二进制差分更新（后期优化）

```text
旧 DLL（哈希 A）+ 差分补丁（适用于 A→B）
                  ↓
          暂存目录中重建新 DLL
                  ↓
          校验目标文件哈希 B
                  ↓
          安全切换后重启进程
```

更新器需要用**实际旧文件哈希**匹配补丁的准确起点；无法匹配、补丁失败或补丁不划算时，回退下载完整目标 DLL。跨多个版本可以直接比较最终目标状态；无须强制应用所有中间补丁。

### 7.3 内容寻址与分块（进一步节流）

教师机按哈希缓存文件（或内容定义分块），多个版本及多台机器共享相同对象。内容定义分块与二进制差分属于不同优化路线，需通过真实 DLL/EXE 发布结果验证收益、复杂度与 CPU 开销。

### 7.4 配置实时同步（不是程序升级）

黑白名单、课堂消息、策略参数、主题等可以由 Agent/API 实时下发，不必走应用二进制更新。替换编译后 DLL/EXE 通常需要重启**对应进程**，不等于热更新，更不一定要重启 Windows。

## 8. 原子更新、回滚与故障恢复（优先于差分）

**禁止在运行中的 SYSTEM Agent 内直接覆写自身 DLL 后假定更新成功。**

推荐流程：

1. 校验发布元数据数字签名、产品/角色/架构/目标版本及防降级规则。
2. 检查本机磁盘空间、运行时、当前进程、更新器协议和必须执行的迁移。
3. 在受 ACL 保护的专用暂存目录下载所有目标文件；续传分段最终须校验完整文件 SHA-256。暂存目录、下载缓存与版本目录应不允许普通学生用户任意写入。
4. 组装一个**完整且内部一致的目标版本目录**；验证所有受管理文件，不允许缺件或依赖版本冲突。
5. 保留上一个可启动版本和必要的回滚元数据；由**独立 UpdateHelper**执行切换（Agent 不能成为唯一恢复执行者）。
6. 停止必要进程/服务，应用需要的服务、任务或注册表迁移，然后切换到目标版本；为 Windows 文件占用、拒绝访问和重启挂起情形设计恢复机制。
7. 启动 Agent、Companion/Executor 等需要的组件；执行健康检查（服务存活、握手、策略读回、版本读回、关键能力）。
8. 只有健康检查完成且结果持久化后才确认成功；否则自动回滚到旧版本，恢复可恢复的系统状态，向教师端报告详细错误。对无法回滚的迁移必须在**开始执行前**阻止自动升级，转入人工维护或额外备份流程。

**当前源码策略：** 现阶段通过完整签名安装器实现“可恢复的联合事务”，而不是把 StudentSetup 改成 `versions/<version>` 指针切换。旧 StudentSetup 移入受保护的同卷恢复目录；SYSTEM Agent 使用独立的 ProgramData 版本目录；固定路径的 Student UpdateHelper 负责 Agent 停止、任务切换、启动、健康读回和回滚。源码状态机、路径和 Windows 验收要求见第 14 节。该策略还没有在 Windows 10/11 上完成故障注入，因此在通过 10.7 门槛前不得部署文件级增量。

**原子性说明：** Windows 多文件覆盖本身不是一个内核级原子操作。项目当前实现的是“持久化日志 + 独立恢复 + 两组件读回 + 失败回滚”的逻辑原子性。后续如切到 `versions/<version>` 加稳定启动入口/指针，必须单独验证服务、计划任务、注册表、进程句柄和断电恢复，不能把目录布局调整误认为已获得原子性。

## 9. 跨版本兼容与必需迁移

更新清单除了文件哈希，还需要标识：

- `minimumUpdaterVersion`：过旧更新器先通过已支持的、签名的兼容路径升级，必要时改用完整安装包。
- `requiredRuntime`：先确保机器安装了所需的 .NET Runtime；Windows 学生机不需要 SDK。
- `requiredMigrations`：服务、计划任务、注册表、配置格式、文件布局等改变，按有序且可恢复的步骤执行。
- `minimumSupportedSourceVersion` / `compatibility`：明确哪些旧版本能直接跨版本升级。
- `rollbackPolicy`：是否允许回滚；迁移涉及不可逆数据变更时必须阻止自动回滚或设计兼容读写与备份。

**特别关注**过去 Self-contained → 共享 .NET Runtime 的变更：只有运行时前置条件满足后才能切换框架依赖应用。卸载应用时不应顺带卸载机器级共享 Runtime。

## 10. 校区内批量更新与灰度

- 教师机汇总学生端版本、文件需求、设备在线状态、空闲/上课状态和更新协议能力。**教师端本身也纳入同一份机制**，但它有独立角色清单、独立健康检查和自更新流程；协调学生机升级时不要同时重启唯一的教师端分发服务。
- **同校区公网只拉取一次**重复内容；缓存文件须以内容哈希索引，并在发给学生端时再次检验。
- 默认先试点 1 台学生机，再推广 3～5 台，最后全教室；发现关键失败立即熔断批次。灰度通过必须以 Agent 版本/健康读回为准，不能以文件下载完成为准。
- 设置并发数、带宽上限、下载超时、可暂停/恢复以及“课间维护窗口”。
- 允许学生机离线：重新上线后查询目标清单并继续，不强迫整个班级同步完成。
- 支持教师端统计：最新版本数、待更新数、下载中、验证中、重启中、成功、回滚、失败和失败原因。
- **不要**要求普通学生用户拥有管理员权限；需要的特权操作应由经验证的 SYSTEM Agent / 独立维护助手执行。

## 11. 安全要求与不变量

- 开发者私钥只保存在受控发布环境，**绝不下发到学生或教师机**。
- 所有清单与补丁索引必须签名；对象必须验证 SHA-256，防止恶意镜像、篡改和缓存污染。
- 拒绝路径穿越（`../`）、绝对路径、重解析点/符号链接逃逸、非允许扩展和更新根目录以外写入。
- 对版本回退、旧签名清单重放、过期清单、错误角色/架构和重复更新命令做防护。
- 校区认证只赋予**授权升级**权限，不能代替开发者发布签名。
- 配置、密钥、策略、日志与用户数据独立于受管理应用目录；清理文件必须基于“旧版拥有记录 + 新版删除规则”，不可扫目录误删。
- Veyon 官方安装器与 .NET Runtime 的升级属于**受控依赖迁移**，不得被普通 DLL 替换逻辑隐式执行。
- 更新过程必须有可审计日志、真实成功/失败状态，不得把“命令已派发”等同于“新版本已验证生效”。

## 12. 开发顺序与当前进度

| 顺序 | 必须交付 | 状态与验收门槛 |
|---|---|---|
| **G0 原子更新门** | 当前完整安装器流程在 Windows 10/11 通过成功、失败回滚、Helper 崩溃、计划任务/ACL 故障、重启恢复和两台机器验收 | 源码已实现；状态协议 v4、事务终态和旧 Agent 门禁已构建；Windows 验收仍未完成。通过前不得开始 G4 文件写入。 |
| **G1 清单与所有权协议** | 冻结 Teacher/Student 角色清单 schema、组件边界、允许删除规则、迁移/兼容数据和签名信任规则 | 2026-10-20 schema v1 和打包所有权边界已提交，见 [file-manifest-v1.md](../specs/cloud-updates-and-installers/file-manifest-v1.md)；Windows CI 实际产物核对仍待做。 |
| **G2 只读计划与清单生成** | 开发机和 Windows CI 生成完整文件清单并对比当前安装；在至少 3 个已知源版本上核对结果 | 只读 CLI 已实现，17/17 本地 Node 检查通过。Windows workflow 已接入真实 Teacher/Student 发布目录与 Helper 清单生成、完整结构校验、Student Agent 双根哈希比对和独立诊断 artifact；需要 workflow 实际运行并审阅 artifact，三源版本计划核对仍待完成。工具无 apply 模式。 |
| **G3 签名清单与不可变对象** | Windows CI 签名精确清单字节；先校验完整 EXE、清单和对象，再发布 CloudBase 索引 | 完整离线 EXE 保留；重复发布不可静默覆盖；清单和每个对象均可独立校验。 |
| **G4 文件级增量** | 仅在 G0 通过后，先对 StudentSetup 下载完整目标文件集、受保护暂存、哈希校验、按文件 journal、保留旧文件并安全切换 | 混合至少 3 个源版本直接升级到同一目标；未知删除、迁移或版本来源不明时走完整安装器。 |
| **G5 缓存与校区分发** | TeacherConsole 按内容哈希缓存、合并各机需求、LAN 分发和限速；学生端再校验；设备按准确的源/目标清单分组 | 重复对象只下公网一次；单机失败不影响其他设备；教师端结果能区分提交、回滚和待核对。 |
| **G6 灰度和运维** | 1 台试点 → 3～5 台 → 全班；提供维护窗口、暂停/恢复、熔断和可审计诊断 | 以 Student Agent 健康和完整目标文件 hash 为成功条件，失败立即停止后续批次。 |
| **G7 二进制差分** | 仅作为文件级方案上的可选传输优化，补丁重建完整目标文件后仍走相同事务 | 至少两个真实版本证明节省大于 CPU/存储成本；补丁错误、基线不符或不划算时回退完整目标文件。 |

**执行原则：** G0 是硬门槛；G1–G3 可在 G0 Windows 验收期间开发并在本地/CI 校验只读计划和发布工具，但不得向生产索引发布或触发设备文件写入；G4 及之后的安装切换必须等待 G0 验收并使用同一套回滚与签名终态机制。完整安装 EXE 始终是首次安装和救援通道。

## 13. 必须覆盖的测试场景

1. 全新 Windows 10/11 虚拟机，没有 .NET SDK / Runtime，完整离线安装及之后升级。
2. 同一教室学生机混跑多个版本；直达同一目标版本，结果一致。
3. 无文件变化、不存在文件、新增文件、目标清单已删除旧文件。
4. 下载中断、断网、续传重复片段、文件损坏、哈希不匹配、镜像返回错误内容。
5. 清单签名无效、清单回放、错产品/架构/渠道、版本降级与过期清单。
6. Agent/EdgePolicyExecutor/Companion 正在运行，文件被占用；SYSTEM 和学生普通用户的启动、SessionId、User SID 匹配。
7. 升级中重启 Windows、断电、杀死 UpdateHelper、磁盘空间不足。
8. 健康检查失败、Agent 无法启动、局域网握手失败、回滚成功和回滚失败时的人工救援方案。
9. 共享 .NET Runtime 升级、服务/计划任务迁移、密钥/配置保护。
10. 54 个校区规模下的公网拉取去重、局域网并发、可暂停、灰度和日志审计。
11. 状态协议 v4 的错误校区、随机数、`commandId`、签名和固定身份；提交/回滚状态与实际 StudentSetup、Agent 版本不一致时必须拒绝。
12. v1/v2/v3 Agent 仍可按兼容路径只读盘点，但不能通过能力缺失或 StudentSetup 版本未知的门禁；不得下发更新命令。
13. 无变更命令也必须获得持久化 `committed` 事务；损坏/ACL 异常 journal 必须返回 `needsReview` 并保留恢复材料，不能推断成功。
14. Teacher 收到成功、已回滚、待核对、状态缺失和超时后，逐台结果与课堂总数一致；滚回旧版本只有在旧安装和旧 Agent 都精确读回时才显示“已回滚”。

## 14. 当前原子事务实现与未来目录演进

### 14.1 当前已实现的完整安装器事务

当前实现使用固定安装路径、版本隔离的 Agent 和独立恢复目录，不使用下方建议的整应用 `versions/<version>` 启动指针：

```text
C:\Program Files\Veyon Campus\Student\                         # 当前 StudentSetup
C:\Program Files\Veyon Campus\UpdateRecovery\Student\<command-id>\
  Previous\                                                     # 事务保留的旧 StudentSetup
  FailedCandidate\                                              # 回滚时隔离的失败候选
C:\Program Files\Veyon Campus\Updater\Student\<target-version>\
  VeyonCampus.UpdateHelper.exe                                  # 不随正在更新的 Agent 一起替换
C:\ProgramData\VeyonCampus\WebsitePolicyAgent\<version>\
  VeyonCampus.Agent.exe                                         # 版本隔离的 SYSTEM Agent
C:\ProgramData\VeyonCampus\WebsitePolicy\<campus-hash>\
  agent-<campus-hash>.json                                      # SYSTEM 保护的校区配置
C:\ProgramData\VeyonCampus\StudentUpdateTransactions\<campus-hash>\
  student-update-transaction.json                               # 单校区原子写入 journal
```

安装器先下载到上述配置目录下以 `student-update-<command-id>` 命名的受保护暂存目录。事务 journal 保存命令 ID、前后 StudentSetup/Agent 版本、配置指纹、签名清单哈希和恢复状态；命令提供的路径不会直接决定目标安装路径。旧目录位于 Program Files 同一卷，便于恢复时移动目录。每次更新由 SYSTEM 全局互斥量串行化，启动恢复任务和看门狗用于接管中断事务。

实际状态机：

```text
Prepared -> PreviousSetupPreserved -> SetupInstallationPending -> SetupInstalled
                                                                  -> AgentStaged
                                                                  -> ActivationPending
                                                                  -> Committed

Prepared -> Committed (no-change command after current version and health readback)

任一未提交状态 -> RollbackPending -> RolledBack
                                  \-> RecoveryRequired -> 重试恢复或人工核对
```

`Committed` 仅在 StudentSetup 角色/版本、SYSTEM Agent 目标版本、计划任务目标路径和配置指纹健康读回后写入。`RolledBack` 仅在旧目录、旧任务路径、旧 Agent 健康和旧版本读回后写入。`RecoveryRequired` 代表无法证明完整恢复，保留 journal、旧文件和诊断材料。已达到目标状态的无变更命令也写入 `Committed` 事务，避免教师端拿不到命令级终态。

TeacherConsole 通过状态协议 v4 读取学生端的 `StudentUpdateProtocolVersion=1`。更新前签名状态记录源 StudentSetup/Agent 版本；每次状态查询带新的随机数和原 `commandId`，Student Agent 对事务状态、前后版本、校区与当前读回值签名。教师端只将匹配固定 Agent 身份且版本一致的 `Committed` 记为成功；一致的 `RolledBack` 显示“已回滚”；版本不匹配、签名错误、journal 异常、状态缺失或超时均进入“待核对”。旧 Agent 的 v1/v2/v3 状态仍可用于查看版本，但不具备 v4 原子终态能力时不能接受教师端远程升级。

**源码构建状态（2026-10-20）：** TeacherConsole Release 构建通过，只有既有 `TextBox.Watermark` 弃用警告；StudentSetup Release 构建通过，0 警告。构建不等于 Windows 安装验收，10.7 的 Windows 故障注入仍是未完成项。

### 14.2 后续文件级更新可评估的版本目录

下面是**候选目标布局，不是当前项目已部署的路径**：

```text
C:\Program Files\Veyon Campus\Student\
  launcher\                 # 稳定入口；供服务/计划任务/快捷方式启动
  versions\
    1.2.2\                 # 上一个可用的完整应用目录
    1.2.3\                 # 暂存验证完成的目标目录
  updater\                  # 独立维护器，不能依赖将被替换的 DLL
C:\ProgramData\VeyonCampus\
  update-state\             # 持久化事务、版本指针、健康结果
  download-cache\          # 受控内容哈希缓存
  config\                  # 配置、校区密钥及其他持久数据（沿用现有实际路径时需迁移）
```

**原子切换的真正含义：** Windows 不支持对一组任意 EXE/DLL 做单一的原子“覆盖事务”。在完整目标目录已校验、旧进程已退出的前提下，切换**单一的版本选择状态/稳定启动入口**；UpdateHelper 使用可恢复日志保证断电后的前进或回滚。服务可执行路径、Startup 快捷方式、计划任务、Edge NativeHost 注册表指向哪个入口，必须一并设计并验证，不能只是把目录重命名。

事务持久化状态（状态名为设计建议）：

```text
Planned -> Downloading -> Staged -> Verified -> Switching
                                             |        |
                                             |        v
                                             |    Activated -> HealthChecking -> Committed
                                             |                        |
                                             +------ failure ----------+
                                                                      v
                                                                  RollingBack
                                                                      |
                                                           RolledBack / NeedsRepair
```

- **允许重试**：下载、断点续传、哈希校验以及尚未提交的暂存过程必须幂等。
- **避免竞态**：同一台电脑同时只允许一个安装事务，命令包含 `commandId`、目标版本和防重放信息；多个教师端/重复点击不能并行覆写。
- **持久化后再执行**：每步先记录预期操作，再执行，再记录结果，断电后由独立的恢复入口接管。
- **限制清理**：旧版本只在新版本健康确认、过了保护窗口且不再被进程占用后清理；至少留一个可恢复基线。
- **注意磁盘容量**：版本目录策略需要临时存放完整目标文件集；下载体积可能仅几 MB，但更新时的磁盘暂存容量可能接近一个完整安装目录。预检必须考虑这一点。

## 15. CI、GitCode 与 CloudBase 的具体交付物

**目标是 CI 构建一次、终端只安装编译产物。** 推荐流水线按顺序：

1. Windows CI 从固定提交、锁文件和固定 SDK 构建教师端、学生端与所有必需的发布子程序。
2. 验证发布目录角色隔离、.NET Runtime 前置条件、版本一致性、无调试符号/秘密材料误入。
3. 对每个角色生成**完整目标文件清单**；文件内容哈希 SHA-256；识别受管理路径及互相依赖的组件组。
4. 计算对象清单和需要的文件下载对象，去重后上传 GitCode Release；继续同时发布完整离线 EXE 供首次安装和旧版升级兜底。
5. 使用受保护的开发者发行私钥签署**精确的清单字节**及必要的信任元数据；发布后核对远端附件内容的大小和哈希。GitHub 提供备用镜像，镜像的对象哈希必须一致。
6. 确保全部附件可读取且验签/验哈希通过后，**最后**发布 CloudBase 的目标版本索引，避免先通知学生机升级、后发现文件还没上传完。
7. 保留发布版本的不可变历史和回滚基线；禁止在同一个版本号下静默替换文件内容。

建议的 Release 元数据与附件（命名示例）：

```text
v1.2.3/
  VeyonCampus-Student-Setup-1.2.3-win-x64.exe
  VeyonCampus-Teacher-Setup-1.2.3-win-x64.exe
  student-manifest.json        # 完整学生端受管理文件清单
  student-manifest.sig         # 验证 student-manifest.json 原始字节
  teacher-manifest.json        # 教师端专用完整清单
  teacher-manifest.sig
  update-objects-0001.zip     # 第一阶段可使用组件 ZIP + 内部对象索引
  update-objects-0002.zip     # 后期可改为独立按哈希寻址的不可变对象
  SHA256SUMS
```

**组件 ZIP 的折中**：阶段一可以先上传 `agent.zip`、`companion.zip`、`edge-policy-executor.zip`，只下载变化的**组件**；这并不等于已经实现精确到单个 DLL 的公网传输去重。阶段二改为对象级索引后，才能仅请求任意文件的 SHA-256 对象。各 ZIP 仍需整体哈希、长度验证、限制解压大小、防 ZIP Slip 和不允许符号链接逃逸。

**复用现有能力**：现有 GitCode 上传脚本只上传完整 Teacher/Student EXE 以及 `SHA256SUMS`；现有 CloudBase 发布元数据的验证流程面向完整安装器。新协议与附件应以向后兼容方式**新增**，不能直接改写旧客户端仍在使用的字段语义。

## 16. TeacherConsole、学生机与版本协议协商

### 16.1 混合版本校区

1. 各学生 Agent 只上报必要信息：`currentVersion`、`updaterProtocolVersion`、`agentCapabilities`、当前安装根及文件完整性摘要；无需上传校区密钥或敏感目录内容。
2. TeacherConsole 获取目标版本签名清单，分别生成 `UpdatePlan(PC-01)`、`UpdatePlan(PC-02)`……；每个计划标明下载对象、前置条件、迁移步骤、目标组件和重启窗口。
3. TeacherConsole 将对象需求合并去重，校区公网只下载一次；学生机从 LAN 获得内容后**自己再次验证**。
4. **当前执行规则：** TeacherConsole 只对报告状态协议 v4 和 `StudentUpdateProtocolVersion=1` 的学生端自动下发完整包更新。缺失能力或 StudentSetup 版本未知的旧 Agent 仅可只读盘点；教师端提示现场管理员核对并运行完整 StudentSetup 安装器。只有未来的旧版兼容路径证明具有同等签名、原子恢复和终态验证后，才可重新开放远程升级。
5. 在教师端完成批量调度前，核对 TeacherConsole 与不同 Agent 版本的通信能力。允许混合版本过渡，但不能假设教师端和学生端必须同一版本才能通信。

`StudentUpdateProtocolVersion=1` 仅表示“完整 StudentSetup 安装器 + SYSTEM Agent 联合事务可报告签名终态”，不代表文件级增量安装已实现。未来清单/文件更新协议需使用独立能力版本和门禁，不能复用此字段推断兼容性。

### 16.2 教师控制台自更新

教师端不能在唯一 LAN 下载服务仍承担大量学生分发时被随意关闭。建议：

- TeacherConsole 下载并验证自身更新，交给独立 UpdateHelper，在**分发任务结束且维护窗口允许时**切换。
- 教师端需要独立的 `teacher-manifest`、更新状态和健康检查（能启动、读取校区配置、管理 Veyon、启动局域网分发服务）。
- 教师端升级失败时，回滚自身版本，继续服务旧学生机；不得因为控制台更新清除校区密钥、名单、班级配置。

### 16.3 Agent 与 UpdateHelper 的职责边界

| 组件 | 负责 | 不负责 |
|---|---|---|
| CloudBase | 有权限的版本索引、渠道、元数据发现 | 存储所有二进制与对设备文件做可信判断 |
| GitCode / GitHub | 发布不可变编译产物和签名附件 | 决定学生机是否被授权升级 |
| TeacherConsole | 校区版本总览、缓存、LAN 分发、灰度调度 | 绕过设备验签、直接操作学生机安装目录 |
| SYSTEM Student Agent | 校区权限验证、状态上报、协调维护窗口、受控启动 UpdateHelper | 在自身进程中直接覆写正在使用的代码 |
| 独立 UpdateHelper | 核验、受保护暂存、版本切换、迁移、恢复、健康检测 | 接受未签名的二进制或无边界的命令脚本 |
| Student Companion / EdgePolicyExecutor | 登录会话 UI、Edge 会话交互及恢复 | 获取 SYSTEM 权限、自行部署升级或修改受保护安装文件 |

## 17. 量化目标、验收门槛和暂不实施内容

以下是**建议验收目标**，不是当前已测出的承诺：

| 指标 | 建议第一阶段验收条件 |
|---|---|
| 正确性 | 混跑至少 3 个旧版本，升级后受管理文件哈希与目标完整清单全部一致 |
| 网络流量 | 同版本文件零重复公网下载，教师端校区缓存按哈希复用；输出每台与全教室节省量 |
| 运行时 | 纯净 Windows 10/11 首装可用；业务增量更新不重复下载已有 .NET Runtime |
| 安全 | 假清单、错误签名、错误角色/架构、路径逃逸、错误对象哈希均在应用前被拒绝 |
| 可靠性 | 断网、杀进程、磁盘不足、文件占用、强制重启后可以恢复/回滚或进入清晰的人工救援状态 |
| 系统策略 | 更新前后管理员/普通用户权限、SYSTEM Agent、Edge 会话刷新、锁屏与黑白名单状态符合预期 |
| 灰度 | 试点失败立即停止后续批次；成功率、错误码、回滚信息在教师端可查询 |

**非目标（第一阶段不做）：** 在运行进程中热补丁 C# 代码、跨公网学生机 P2P、复杂内容定义分块、无限制镜像源选择、为所有历史版本生成两两二进制差分、在没有证据的情况下强制移除系统 .NET Runtime。

## 18. 给 Codex 的实施清单与严格执行门

本节是执行顺序，不再把“只读诊断工具”误写成整个更新项目的终点。每项工作单独提交；未通过的现场门槛保持未完成，不能因为代码已合并或 CI 构建成功就自动放行。

### G0：完成完整安装器的原子更新验收（当前阻断增量更新）

- [x] 持久化 SYSTEM journal、全局互斥、固定受保护路径、旧 StudentSetup 备份、Agent 版本隔离、恢复任务、看门狗和 Helper 失败回滚。
- [x] 增加状态协议 v4、能力 `StudentUpdateProtocolVersion=1`、命令 ID 绑定的事务读取、前后版本读回、签名验证和固定身份检查。
- [x] 教师端对旧协议/未知 StudentSetup 版本关闭远程自动更新；按事务终态显示“成功 / 已回滚 / 待核对”；无变更命令也写终态 journal。
- [x] macOS 上分别构建 TeacherConsole 与 StudentSetup；这只验证编译和角色隔离。
- [ ] 在 Windows 10 与 Windows 11 虚拟机或专用测试机实际完成成功升级，记录 StudentSetup/Agent/计划任务/ACL/配置指纹/Agent 身份前后状态。
- [ ] 在以下持久化边界注入故障并复验：安装器启动前、StudentSetup 目录移动后、安装器运行中、Agent 暂存后、任务切换后、Helper 等待旧进程时、候选 Agent 启动后、健康读回前后、提交写入时。
- [ ] 另行注入磁盘不足、目标文件占用、SYSTEM 任务创建/ACL 错误、杀死 Helper、强制重启/断电；每种情况必须落入 `Committed`、`RolledBack` 或 `RecoveryRequired` 之一，不能留下无解释的混合安装。
- [ ] 核对成功时 StudentSetup 与 Agent 都是目标版本；回滚时两者、任务动作、配置指纹和设备身份都与更新前一致；`RecoveryRequired` 必须保留旧文件、journal、任务/日志并给出管理员救援步骤。
- [ ] 使用至少两台设备验证教师端签名命令、LAN 下载、双端口/防火墙、并发更新和逐台最终状态。旧 Agent 没有 v4 能力时必须被阻止，不得通过旧版本号状态猜测成功。

**G0 放行条件：** 上述 Windows 失败注入和两台设备记录齐全，任务 10.7 的审阅结论为通过。未通过时，继续修复全包事务；不得开始任何会写入学生安装目录的文件级更新。

### G1：盘点真实发布产物并冻结清单协议

1. [x] 从 `scripts/package-windows-offline.ps1`、教师/学生 Inno Setup 脚本和 CI workflow 盘点安装根；实际运行时产物仍待 Windows CI 生成核对。
2. [x] 冻结完整目标清单字段和角色/架构/组件所有权；机器路径只能由固定 root ID 推导。
3. [x] 增加 JSON Schema、16 MiB/50,000 文件上限、原始字节 detached signature 规则、密钥轮换和防降级约束。
4. [x] 明确来源清单缺失或文件不匹配时标记 `unsupported-source`；只允许依据精确源清单摘要和旧文件哈希删除受管文件。
5. [x] 将文件更新协议与 `StudentUpdateProtocolVersion=1` 分开；本阶段仅定义协议和计划，不具备安装权限。
### G2：先交付只读安装计划和 CI 清单生成

1. [x] 新建可在 CI 和开发机运行的只读清单生成/比对工具。扫描只读，不启动安装器、不删除、不替换、不写 Program Files。2026-10-20：CLI 已生成完整无签名清单并输出只读计划；支持本地显式开发模式，计划固定为 `deployable=false`。
2. [x] 输出每个角色的当前安装摘要、目标摘要和 `unchanged / download / add / managed-delete / migration-required / unsupported-source` 计划，并明确每项 hash、大小、原因和所需协议。17/17 本地 Node 检查通过；Windows workflow 已加入此套件，等待远端 Windows 结果。
3. [x] 为旧版缺少完整文件清单的安装设定安全基线：通过受信任完整安装器迁移，或标记人工处理；不能用猜测出来的目录内容建立可信所有权。
4. [ ] 在 Windows CI 中从正式发布目录生成清单，核对 EXE、DLL、资源文件、Helper 和依赖版本；保留完整离线 EXE。2026-10-20：workflow 已加入生成和校验步骤并单独上传 unsigned diagnostic artifact；需运行 CI、查看实际清单报告后才能验收。
5. [ ] 只有只读差异计划在至少 3 个源版本和目标版本上核对正确，才进入发布附件开发。

### G3：发布签名清单与不可变对象

1. Windows CI 构建一次正式 payload，生成完整目标清单；对带域分隔符的清单原始 UTF-8 字节做开发者签名。
2. 先发布并验证完整 EXE、清单和对象，再最后发布 CloudBase 目标版本索引；同一版本附件不允许静默替换。
3. TeacherConsole 只缓存经过签名验证的清单和经过长度/SHA-256 验证的对象；缓存键至少包含产品、角色、架构、源/目标版本和清单摘要。
4. 镜像回退只能换下载地址，不能改变签名、对象哈希或允许的产品范围。

### G4：在已验收的原子事务内实现 Student 文件级增量

1. 先生成 StudentSetup 文件清单，再按确切源版本读回逐台计算目标计划；合并全班对象需求，教师公网重复对象只拉取一次。
2. Student Agent 对教师分发内容再次验证签名清单、角色、架构、目标版本、路径白名单、对象长度和 SHA-256；不得信任教师缓存本身。
3. 在受保护暂存区重建完整目标文件集；缺文件、未知删除、路径逃逸、重解析点、未授权迁移或磁盘空间不足均在更改活动目录前终止。
4. 在同一原子事务中记录每个替换/新增/删除操作与旧文件恢复材料；先切换、再启动并健康检查 StudentSetup/Agent 所需组件；仅完整目标状态读回后提交。
5. 复用 G0 的回滚/状态终态协议。任何文件变更失败都必须恢复完整旧目标；无法确认恢复时进入 `RecoveryRequired`，教师端显示待核对。
6. 完整安装器继续作为首装、旧协议迁移、缺少兼容清单和管理员救援方式；增量失败不得清除或覆盖该通道。

### G5：内容缓存与校区内分发

1. Teacher 按不可变 SHA-256 对象缓存，并以角色、架构、精确源/目标版本和清单摘要隔离更新计划；同内容跨设备/版本复用。
2. 汇总学生设备各自的需求集合后只下载缺少的对象；LAN 发送前与发送后分别校验长度和 SHA-256。
3. 为并发、带宽、超时、暂停/恢复、磁盘空间和缓存回收设置上限；只在相关事务提交后清理对象，失败设备保留诊断需要的状态。
4. 缓存和分发失败只影响对应对象/设备；完整离线安装器仍可救援，镜像回退不能改变已签名清单和对象哈希。

### G6：灰度运行、指标与暂停/熔断

- 一台试点学生机；验签、哈希、策略、开机恢复和回滚全部通过后再扩大到 3～5 台，然后全班。
- 任何签名失败、文件集不完整、`RecoveryRequired`、校区关键能力下降或回滚失败立即停止新批次。
- 记录每台公网/局域网流量、缓存命中、更新时长、健康结果、回滚原因和人工操作；不收集学生个人信息。
- 支持维护窗口、暂停/恢复、离线设备重连后重新规划、并发上限和管理员终止操作。

### G7：评估二进制差分（最后）

只有 G0–G6 的文件级更新在 Windows 实机完成多版本、灰度与故障验收后，才比较 bsdiff/zstd 等差分路线。差分仅是传输优化：必须匹配确切旧文件哈希，在暂存目录重建目标文件并核对完整目标 SHA-256；任何差分错误、源版本不匹配或收益不足立即回退完整目标对象。绝不把补丁直接应用到正在运行的二进制或活动安装目录。

---

**一句话原则：把“下载最少文件”做成可选优化，把“无论旧版本多老、网络多差、更新何时中断，都不会轻易损坏校区管理能力”做成更新系统的强制保证。**
