# 校区配置包 Schema 迁移与回滚语义（CM-03）

> 2026-11-04 20:42 定义。依据 `src/VeyonCampus.Core/PackageManifest.cs`、`PackageBuilder.cs`、`PolicyTrustRotation.cs` 的现有事实。
> 状态：`SPEC-DEFINED; CODE-WIRED; TEST-DEFERRED; VM-PENDING`。**本文件只定义语义与门槛，不宣称已在 Windows VM 执行过 7→8 迁移或回滚。**

## 1. 已发布版本事实（不推测）

| schemaVersion | 已发布内容 | 现有校验入口 |
| -- | -- | --- |
| 1–6 | 校区公钥与逐版本增加的策略公钥 / 兼容范围 / 建议操作；1 允许内嵌安装器 | `PackageManifest.Load` |
| **7** | 在 5/6 基础上增加 `teacherChannel`（1–16 个地址 + `.cer` TLS 根证书），完整网站 / 应用 / 系统策略公钥与 Student Agent 兼容范围；**无** `policyTrustRotations` | `PackageManifest.Load` 的 `version >= 7` 分支、`StudentAgentChannelProtocol.ValidateBootstrapConfiguration` |
| **8** | 7 的全部内容 + **1–3 个** `policyTrustRotations` 签名授权；授权校区必须匹配、密钥用途不重复、授权中的新公钥必须与包内对应策略公钥逐字节一致；`teacherChannel` 必须完整 | `PackageManifest.Load` 的 `version >= 8` 分支、`PackageBuilder.BuildCore` |
| **9** | **未发布**：没有字段结构、没有 verifier、没有 writer | 无 |

`PackageManifest.Load` 明确写着 `schemaVersion=2 至 8 只允许携带校区配置`，因此 **8 是当前最高已发布版本**。

## 2. 允许清单（唯一实现：`CampusPackageSchemaMigration.Plan`）

| 变换 | 处置 | 理由 |
| ---- | ---- | ---- |
| 同版本（1–8） | `NoMigration` | 按原 schema 校验并读回，不做任何字段变换 |
| **7 → 8** | `ExplicitRebuild7To8` | 唯一允许的实际升级，见第 3 节 |
| 8 → 9 / 任意 `to > 8` | `Rejected` | 目标 schema 未发布，**不虚构字段、不伪造迁移结果** |
| 任意降级 `to < from` | `Rejected` | 新 schema 字段无法由旧包反推，且会让旧根提前退休 |
| 跳级 `to > from + 1` | `Rejected` | 必须逐级迁移，每级都要原子替换与读回校验 |
| 其它已发布版本对 | `Rejected` | 没有已定义的字段变换，不自动补全 |

计划对象同时声明 `RequiredInputs`、`RequiresSignedRotationAuthorizations`、`RequiresAtomicReplace`、`RequiresReadbackVerification`、`RequiresOriginalByteRestore`、`AllowsImplicitFieldInference`。**任何计划都不允许隐式字段推导**。

## 3. 7→8 迁移语义（唯一允许的实际变换）

**性质**：这是一次**由操作者显式驱动的包重建**，不是把旧 ZIP 里的 JSON 补几个字段。旧包里根本没有候选根、双签名授权和退休时间，静默推导必然产生无效信任链。

**必须人工提供的输入**（`SevenToEightInputs`）：

1. 完整 `teacherChannel`：1–16 个连接地址与 `.cer` TLS 根证书（沿用 schema 7 的全部约束）；
2. 网站 / 应用 / 系统策略公钥与 Student Agent 兼容范围（schema 7 的完整载荷不得裁剪）；
3. 1–3 个 `PolicyTrustRotation` 双签名授权：校区、密钥用途、旧根指纹、候选根指纹、退休时间；
4. 包内对应策略公钥与授权中的新公钥**逐字节一致**。

**执行顺序**（与 `MaintenanceServiceConfigurationStore.MigrateLegacyBootstrap` 的 1→2 范式一致）：

1. 读旧包 → 按 schema 7 完整校验 → **保留原始字节快照**（不可变，用于失败恢复）。
2. 生成候选根与双签名授权；旧根在候选阶段**保持活动**，不得提前退休。
3. 由 `PackageBuilder.BuildCore` 生成 schema 8 包；构建入口调用 `CampusPackageSchemaMigration.RequireExplicitRebuildInputs` 阻断缺少授权、teacherChannel 或系统策略公钥的请求。
4. **原子替换**（先写临时文件再原子移动），不就地改写旧包。
5. **读回校验**：`schemaVersion == 8`、`policyTrustRotations` 1–3 项、校区 / 固定身份 / 配置指纹 / `teacherChannel` 与预期一致，授权的新公钥与包内公钥一致。
6. 任一步失败 → **恢复原始字节**，保留旧包与旧根，记录失败阶段；不得留下半迁移状态，不得继续下发。
7. 旧根退休只能在全部设备确认新根后、按授权中的退休时间显式执行；撤销时同样先备份再读回。

## 4. 回滚与恢复

- **回滚对象**：整份配置包字节，不是单个字段；回滚后必须重新按 schema 7 校验并读回。
- **触发条件**：构建失败、授权验签失败、原子替换失败、读回不一致、设备侧拒绝或超时。
- **禁止**：用 schema 8 包反推 schema 7；在未恢复旧包前下发新授权；在旧根仍被设备依赖时提前退休。
- **审计**：记录来源 / 目标版本、操作者、授权指纹、旧包与新包 SHA-256、失败阶段、恢复结果；不记录私钥。

## 5. 8→9 明确不定义

`ReservedNextVersion = 9` 只作为占位常量存在，**没有任何字段结构或变换实现**。`Plan(8, 9)` 返回 `Rejected`，理由写明"未发布字段结构，不得虚构目标 schema"。后续若要发布 9，必须先同时具备：字段规范、verifier、writer、逐级迁移计划、失败恢复与 VM 验收证据；缺一不可开启该路径。

## 6. 与现有代码 / 其它任务的关系

- `src/VeyonCampus.Core/CampusPackageSchemaMigration.cs`：允许清单与前置输入守卫（本轮新增）。
- `src/VeyonCampus.Core/PackageBuilder.cs`：生成 schema 8 包时调用守卫接线（本轮新增）。
- `src/VeyonCampus.Core/PolicyTrustRotation.cs` / `PolicyTrustRotationDraftStore.cs`：候选根、双签名授权与草稿持久化，属 **CM-05**；本文件不重复其密码学实现。
- `src/VeyonCampus.Core/Maintenance/MaintenanceServiceConfigurationStore.cs`：bootstrap 1→2 迁移范式（原子替换 + 读回 + 原字节恢复），是本文件第 3 节的参照。
- 主计划附录 A 的 CM-03、附录 C 的阻塞清单按本文件口径更新。

## 7. 验收门槛（`TEST-DEFERRED`）

- [ ] `Plan` 的每种变换（同版本 / 7→8 / 8→9 / 降级 / 跳级 / 未知版本）有定向夹具结果。
- [ ] 缺少任一必需输入时 `PackageBuilder` 拒绝生成 schema 8 包。
- [ ] 7→8 在隔离环境执行：原始字节快照、原子替换、读回校验、故障注入后原字节恢复均有证据。
- [ ] 旧根退休与撤销恢复在 Windows VM 实测；production 与真实校区推送仍按单独授权门槛。
