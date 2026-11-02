# 历史 Student Agent 兼容性矩阵（P0-02）

核查日期：2026-11-03。状态：**未完成，受真实 Windows VM 和独立 CloudBase 测试环境阻塞**。

本表严格区分标签源码、GitHub 发行页元数据、自动化夹具和实机证据。标签中的 `.csproj` 版本不是已安装二进制读回；网页显示的 SHA-256 不是本机下载后复算的结果。不得据此把兼容性记为已验收。

## 目标历史版本与发行资产

| StudentSetup | Git tag / commit | 标签源码内 Agent 项目版本 | GitHub 安装包发行页显示的 SHA-256 | 本机二进制读回 |
| --- | --- | --- | --- | --- |
| 1.1.40 | [`v1.1.40` / `72e9fe1`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.40) | `0.4.55` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.40)：`9e2fc290e2030be59b4172fdc8cc410a0f9ba1d360862b483abf53b5e7642e1f` | **未取得** |
| 1.1.41 | [`v1.1.41` / `18727c3`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.41) | `0.4.55` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.41)：`0c5f65c9eba198de99a98dd2c524d8261a85b243bad0435506c32ae28cb245ba` | **未取得** |
| 1.1.44 | [`v1.1.44` / `b4180f3`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.44) | `0.4.56` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.44)：`59787c18b77e1b3f3f366e5334de01246b55ee21ffc6e9c49d7d30af2a1b08f2` | **未取得** |

以上项目版本来自各 tag 的 `src/VeyonCampus.Agent/VeyonCampus.Agent.csproj`。发行页显示的安装包大小约 64.9–65 MB，页面也分别显示上述 SHA-256；本次未能下载资产，因此尚未核对本地文件摘要、Authenticode 签名、安装包内部 StudentSetup 版本或 Agent 产品版本。仓库中没有 `v0.4.55` 独立 tag；0.4.55 的源码版本依据是 1.1.40/1.1.41 的标签内容。

## 自动化与源码核查

| 检查 | 结果 | 能证明什么 | 不能证明什么 |
| --- | --- | --- | --- |
| `dotnet run --project tests/VeyonCampus.Checks -c Release --no-restore -- --student-application-update-fixtures` | **通过**：`PASS Student Agent capability matrix, version binding, rebase safety, and handoff rollback fixtures` | 当前代码的合成兼容性矩阵、旧 Agent `0.4.55/0.4.56` 稳定签名 HTTP 选择、本机测试能力/测试公钥指纹门禁、1.1.40 指纹迁移判断和旧 Setup 版本别名保护逻辑可通过夹具 | 不执行历史二进制、不监听真实 TCP 39174、不完成 Windows 文件/任务操作，也不验证旧 Agent 的真实签名行为 |
| `npm run check`（`cloudfunctions/veyon-api`） | **通过 22/22** | 本地 CloudBase 契约 double 覆盖发行 API；检查 v1–v3 旧响应不带 Agent target，v4 返回 Schema 3 正式发行与分离 sidecar；静态检查本次 SQL migration 与部署脚本包含迁移 | 不访问真实 CloudBase，不证明迁移已执行、API 已部署、线上数据库有签名 target 或生产/测试发行查询可用 |
| 默认 .NET 检查运行至 `IssueThirdRoundChecks.Run` | 相关发布检查先通过；随后**整套运行失败**，错误为 `system policy guidance remains platform-specific` | `ApplicationReleaseChecks` 的合成发行验签、摘要、sidecar 及暂存/回滚检查在失败前完成 | 整套 .NET 检查不能标记为通过；失败项不属于本次 P0-02 的历史 Agent 实机基线 |
| tag 源码审查 | **部分完成** | 三个标签源码声明的 Agent 版本及旧 `/v1/update` 路径存在；当前实现保留 0.4.55/0.4.56 的正式版旧 HTTP 回退 | 标签源码不能代替发行二进制或当时部署到学生机上的文件读回 |

当前兼容代码要求正式 `stable` 发行继续使用旧 Agent 可验签的 Schema 3；旧 Agent 走旧 `/v1/update` 时仍须验证稳定发行签名和摘要。`local-test` 需要 Schema 4、新更新协议和匹配的本机测试公钥指纹；测试夹具对缺失/不匹配能力返回不支持，不绕过验签。这是**当前代码的自动化行为**，不是在 0.4.55/0.4.56 二进制上的实测结果。

### 历史标签源码边界（非二进制验收）

对 `v1.1.40`、`v1.1.41`、`v1.1.44` 分别执行了 `git show <tag>:src/VeyonCampus.Core/ApplicationReleases.cs` 和 `git show <tag>:src/VeyonCampus.Core/WebsitePolicyRuntime.cs`：三份历史发行验证器均只接受 Schema 1/2/3，默认从 `ApplicationReleaseTrust.LoadPinnedPublicKeyPem()` 加载 Developer Release 公钥；三份历史运行时源码均保留 `StudentUpdatePath = "/v1/update"`。因此按**这些标签源码**，Schema 4 local-test manifest 不会进入旧发行验证器；正式 Schema 3 是可解析版本。

这项核对没有读取已发布安装包的程序集，也没有对旧 Agent 实际使用的嵌入公钥、真实签名状态回执、TCP 39174 或完整更新事务做实机验证。实际发行包与标签源码是否一致仍待从下载后的二进制和 VM 读回确认。

## Windows VM 验收记录

每行必须用真实历史安装器建快照，并从安装后的二进制读取版本。以下必测项均尚未运行：签名状态请求、TCP 39174、`/v1/update`、发行公钥和摘要校验、安装事务/回滚、稳定 Schema 3 升级、Schema 4 测试包拒绝、1.1.40 配置指纹恢复和旧 Setup 版本别名目录处理。

| 快照 | Setup/Agent 实际二进制读回 | 签名状态 + TCP 39174 | 正式 Schema 3 + `/v1/update` | local-test Schema 4 拒绝 | 事务/回滚 | 1.1.40 指纹迁移及别名 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A | 待建：目标 1.1.40 / 0.4.55 | 未测试 | 未测试 | 未测试 | 未测试 | 未测试 | **NOT-TESTED** |
| B | 待建：目标 1.1.41 / 0.4.55 | 未测试 | 未测试 | 未测试 | 未测试 | 不适用/需确认旧 Setup 别名 | **NOT-TESTED** |
| C | 待建：目标 1.1.44 / 0.4.56 | 未测试 | 未测试 | 未测试 | 未测试 | 不适用/需确认旧 Setup 别名 | **NOT-TESTED** |

执行时每个快照需保留：安装器来源与下载摘要、签名证书链、安装前后 Setup/Agent 文件版本、Agent 运行路径与 PID、TCP 监听读回、签名请求和回执（脱敏）、更新 commandId、事务 journal 状态、回滚材料及健康读回。遇到签名/身份/事务不匹配时停止，不降低验签门槛、不清理恢复材料。

## CloudBase 测试环境核查

| 项目 | 本机证据 | 环境执行结果 |
| --- | --- | --- |
| `20261102120000_support_signed_student_agent_targets.sql` | migration 文件存在；本地 Node 测试检查字段、签名绑定函数和 `service_role` 权限 | **未部署/未验证** |
| `/v4/releases/latest` | API 路由和本地 contract-double 测试存在；v4 响应把 target sidecar 与旧 Schema 3 manifest 分离 | **未查询测试环境** |
| 发行构建 sidecar / 签名验证 | 教师端有独立 target sidecar 验签和 Setup SHA-256 绑定；夹具使用临时合成 RSA 密钥 | **真实发行 sidecar 未验签** |

仓库 [cloudbaserc.json](../../cloudbaserc.json) 配置的唯一 `envId` 是 `veyon-control-d3gs8hmuyd09c00a7`，对应 production；仓库未发现独立 test/staging `envId`。本次没有对 production 执行 CLI、迁移或部署操作。CloudBase CLI 技能要求先由用户明确确认目标环境 ID，且本计划要求在测试环境验收，因此线上 mock/源码检查不作为部署证据。

## 阻塞与继续条件

- 当前工作机是 macOS arm64；未发现 Windows VM 管理器或 VM 命令（`pwsh`、QEMU、VirtualBox、VMware、UTM、Multipass、Lima、Wine）。已安装的 `Windows App` 是远程桌面客户端，不是 VM。
- 工作区 `artifacts` 没有可用于这三组历史版本的独立 Setup 安装器。尝试访问 GitHub 发行二进制时，本机 `curl` 在 DNS 解析阶段超时；浏览器发行页元数据不能替代本地二进制。
- 当前唯一配置的 CloudBase 环境为 production；执行 P0-02 所需的隔离测试环境 ID 未提供。
- **解除阻塞所需输入：** 可访问的三组 Windows VM/快照（或能在本会话使用的 Windows 主机和经核验安装器），以及独立 CloudBase 测试环境 `envId`。拿到后从 A 快照开始，按表逐项验收并保存原始证据；未完成前不进入 P0-03。
