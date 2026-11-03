# 历史 Student Agent 兼容性矩阵（P0-02）

核查日期：2026-11-03。状态：**未完成；缺少真实 Windows VM 证据，且 production 尚未部署本次迁移与 v4 API**。

本表严格区分标签源码、GitHub 发行页元数据、自动化夹具和实机证据。标签中的 `.csproj` 版本不是安装器内部二进制读回；本机 SHA-256 与公开摘要一致证明下载字节匹配公开发行资产，但不能替代 Authenticode 验证或 Windows 安装读回。

2026-11-03 范围调整：用户确认线上主流 Student Agent 为 `0.4.56`，当前修复与 VM 验收优先覆盖 `1.1.44 / 0.4.56`。`0.4.55` 的历史行保留作记录，但暂不作为本轮门槛；若线上装机盘点发现该版本，再恢复对应验收。

## 目标历史版本与发行资产

| StudentSetup | Git tag / commit | 标签源码内 Agent 项目版本 | 发行页公布的 SHA-256 | 本机下载后 SHA-256 | PE ProductVersion / Authenticode |
| --- | --- | --- | --- | --- | --- |
| 1.1.40 | [`v1.1.40` / `72e9fe1`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.40) | `0.4.55` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.40)：`9e2fc290e2030be59b4172fdc8cc410a0f9ba1d360862b483abf53b5e7642e1f` | 与 GitHub Release API 一致 | `ProductVersion=1.1.40`；PE 证书表为空；**安装后 Agent 版本未读** |
| 1.1.41 | [`v1.1.41` / `18727c3`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.41) | `0.4.55` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.41)：`0c5f65c9eba198de99a98dd2c524d8261a85b243bad0435506c32ae28cb245ba` | 与 GitHub Release API 一致 | `ProductVersion=1.1.41`；PE 证书表为空；**安装后 Agent 版本未读** |
| 1.1.44 | [`v1.1.44` / `b4180f3`](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/v1.1.44) | `0.4.56` | [Student Setup asset](https://github.com/Ljc798/veyon-campus-deployment/releases/expanded_assets/v1.1.44)：`59787c18b77e1b3f3f366e5334de01246b55ee21ffc6e9c49d7d30af2a1b08f2` | 与 production `/v3` manifest 一致 | `ProductVersion=1.1.44`；PE 证书表为空；**安装后 Agent 版本未读** |

以上 Agent 项目版本来自各 tag 的 `src/VeyonCampus.Agent/VeyonCampus.Agent.csproj`；每个安装器 PE 的 `VERSIONINFO` 资源也显示对应的 `ProductVersion`。三个历史安装包的实际文件摘要均已复算匹配：1.1.40 为 68,048,819 字节，1.1.41 为 68,126,316 字节，1.1.44 为 68,144,138 字节。`llvm-readobj --file-headers` 对三个 PE 均读到 `CertificateTableRVA=0`、`CertificateTableSize=0`，因此安装器文件没有嵌入 Authenticode 签名，也没有可验证的内嵌发布者证书链；这不等同于 Student Agent 的 RSA 发布清单签名。安装器均未运行，实际安装后的 Agent 文件版本仍未读回。仓库中没有 `v0.4.55` 独立 tag；0.4.55 的源码版本依据是 1.1.40/1.1.41 的标签内容。

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

2026-11-03 用户截图补充：教师端显示 StudentSetup `1.1.44`、Agent `0.4.56`。选择 local-test 更新时，教师端收到的签名能力回执未声明本机测试发行公钥，因此在下发前拒绝；测试包未发送。此结果证明教师端没有绕过能力/信任门禁，但**没有测试旧 Agent 自身对 Schema 4 的拒绝处理，也没有测试 stable Schema 3 更新、安装事务或回滚**。操作系统版本、学生端二进制路径、TCP 监听读回及原始日志尚未记录；矩阵结论仍为 `NOT-TESTED`。

执行时每个快照需保留：安装器来源与下载摘要、签名证书链、安装前后 Setup/Agent 文件版本、Agent 运行路径与 PID、TCP 监听读回、签名请求和回执（脱敏）、更新 commandId、事务 journal 状态、回滚材料及健康读回。遇到签名/身份/事务不匹配时停止，不降低验签门槛、不清理恢复材料。

## CloudBase 真实环境核查（唯一配置环境为 production）

| 项目 | 本机证据 | 环境执行结果 |
| --- | --- | --- |
| PostgreSQL migration | 本地存在 `20261021140000_create_file_update_release_catalog.sql` 与 `20261102120000_support_signed_student_agent_targets.sql`；通过 `tcb db pg migration list --remote-only --json` 实查 production，远端 21 项、最新 `20261018100000` | **以上两项均未应用；没有改动线上数据库** |
| `veyon-api` 部署状态 | `tcb fn list` 实查：函数为 Active、Nodejs20.19，云端修改时间 `2026-10-30` | 只读元数据；未部署本地代码 |
| PostgreSQL migration 预演 | `tcb db pg migration up --dry-run --json` 返回 `dryRun=true`、`executable=false`；两条待迁移为 `20261021140000_create_file_update_release_catalog`、`20261102120000_support_signed_student_agent_targets` | **仅预演，没有执行写入**；同时报告本地 `20261010090000_add_daily_operations_reporting` 早于远端最新 migration 的冲突。部署脚本注释说明该 OperationsReporting migration 目前禁用并排除。CLI 3.8.5 帮助列有 `--include-all` 越序选项，但尚未验证它实际选择哪些文件；不可盲用 |
| allowlist migration helper | 新增 `scripts/apply-cloudbase-release-migrations.sh`，生成仅含两条目标 SQL 的临时目录，并通过 CloudBase CLI 的 `database.migrations` 配置选择该目录；production envId 被拒绝，应用模式要求二次确认 | **尚未执行或测试**；仍需在独立测试 envId 用 `--dry-run` 确认预演只包含目标 ID，再由用户明确执行应用并验收 ledger/schema |
| allowlist helper 离线契约夹具 | 新增 `scripts/apply-cloudbase-release-migrations.test.cjs`，覆盖 allowlist、确认门禁、production 拒绝、基线缺失/禁用迁移、预演冲突及应用后 ledger 读回；已接入 API contract CI | **本轮未运行**，按用户要求留给用户/CI 验证；不连接 CloudBase |
| production `/v4/releases/latest` | 用仓库 DNS resolver 发起真实 HTTPS GET，返回 `404 {"error":"Not Found"}` | **线上 v4 路由不可用** |
| production `/v3/releases/latest` | 真实 HTTPS GET 返回 `200`：StudentSetup `1.1.44`、Schema 3，发布时间 `2026-11-01T12:55:59.640884+08:00` | 仅证明旧版发行查询可用；Schema 3 响应没有 Agent target sidecar |
| 发行签名 / sidecar 验证 | 本机尝试使用 `ApplicationReleaseClient.Verify` 验证线上 v3 响应；当前工作区没有嵌入或提供 Developer Release 公钥，验证器在加载公钥时安全停止 | **线上 v3 签名未验证；真实 target sidecar 不可取得、未验签** |

仓库 [cloudbaserc.json](../../cloudbaserc.json) 只配置 `veyon-control-d3gs8hmuyd09c00a7`。用户明确要求使用真实环境后，本次对该 production 环境做了只读查询；没有应用 migration 或部署 API。远端 migration ledger 显示最新版本为 `20261018100000`，因此目标迁移 `20261102120000` 尚未应用；新路由线上返回 404。production 结果已经证明新增功能尚未上线，但不能代替隔离测试环境中的迁移、v4 响应和真实签名验收。

## 阻塞与继续条件

- 当前工作机是 macOS arm64；未发现 Windows VM 管理器或 VM 命令（`pwsh`、QEMU、VirtualBox、VMware、UTM、Multipass、Lima、Wine）。已安装的 `Windows App` 是远程桌面客户端，不是 VM。
- 三个安装器已从 production artifact / GitHub release 下载到 `/tmp` 并复算匹配 SHA-256；PE `ProductVersion` 与发行版本一致。三个文件的 PE 证书表均为空，没有内嵌 Authenticode 签名；文件尚未运行或安装，安装后的 Agent 版本仍未读回。
- 当前唯一配置的 CloudBase 环境为 production；本次已做只读实查，结果确认 migration 与 v4 路由没有部署。工作区也没有 Developer Release 公钥，因此暂时无法从教师端验证器验签线上发行。
- `migration up --dry-run` 没有执行写入，确认两条本次所需迁移待应用；默认预演另报告旧的 OperationsReporting 本地迁移顺序冲突。部署脚本刻意排除该未启用迁移，仍须在隔离测试环境确认受支持的部署路径后才能验收。
- 新增问题 [012](../问题跟踪/README.md#012cloudbase-迁移顺序冲突缺少安全的选择性应用路径)：CLI 提供 `--include-all` 越序选项，但实际执行集合未确认；它可能包含部署脚本明确排除的禁用迁移。未设计并隔离验证选择性应用与 ledger 策略前，不运行该选项。
- **解除阻塞所需输入：** 可访问的三组 Windows VM/快照（或能在本会话使用的 Windows 主机和经核验安装器）、隔离 CloudBase 测试环境，及用于发行验证的 Developer Release 公钥。若决定用 production 部署，则需先完成单独的生产变更审查和部署授权；本次仅执行只读测试。
- 获取上述条件后，从 A 快照开始，按表逐项验收并保存原始证据；未完成前不进入 P0-03。
