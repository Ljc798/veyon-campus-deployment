# 座位图重构设计评审补丁 v2.2（第 2 版）

> 日期：2026-11-05
> 对象：`2026-11-05-seat-directory-refactor-design-v2.1.md`
> 状态：**本文件为第 2 版，替代同名第 1 版**。第 1 版中 P-01、P-05 的写法被判定不可行，已重写；P-03、P-06 已按评审意见调整。
> 证据基线：`develop` / `f498b88`

## 修订记录

| 项 | 第 1 版写法 | 第 2 版 |
| --- | --- | --- |
| P-01 | 检测到重复 UUID 就自动 `config export → 去重 → config import → 失败恢复` | **降级为四步只读 + 阻断**；修复器独立、需确认、需指纹校验、需读原始配置、需 VM 验证后才开放 |
| P-02 | 保留"删除学生电脑（含 Veyon）" | 不变（采纳） |
| P-03 | 用 grep 静态断言替代 R16 | **保留静态守卫 + 补行为测试**；静态搜索不替代行为验证 |
| P-04 | S2 先出内部测试包 | 不变（采纳） |
| P-05 | "首次建档写 `location.ObjectId`"——一行赋值 | **不可编译**：`VeyonNetworkLocation` 没有 `ObjectId`，且数据源根本没有 UUID。改为"重建地点读取模型"，列出 5 条改动 |
| P-06 | "1 个座位尚未在 Veyon 中 [立即新增]" | **文案错误**：未绑定座位不等于缺电脑。改为中性表述，不把未绑定当待办 |
| P-07 | 真实档案迁移夹具 | 不变（采纳） |

---

## A. 前一轮的事实纠正仍然成立

（保留第 1 版 A 节结论，不再重复论证）

- C-01 `boundSeats == Targets.Count` 仍存在：`TeacherViewModel.cs:4694`
- C-02 `Reconcile()` 有文件锁与写入副作用：`VeyonDeviceDirectoryStore.cs:80,222`
- C-04 删除后立即落盘：`TeacherViewModel.Classroom.cs:1662`
- C-05 删除连带删 Seat：`:1632-1635`

---

## B. P-01（重写）：只做检测与阻断，不自动改写整份 Veyon 配置

### B.1 为什么第 1 版不可行

1. **`config import` 是配置级覆盖写**，不是"只改一个 Location"。它无法证明除目标条目外其他字段未被改变——即使电脑数量相同，Host、父地点关系、其他配置节点都可能被改写。
2. **自动恢复是第二次整份写**。导入失败后再 `import` 备份，同样可能失败，也可能覆盖这期间的其他修改。把两次不确定写入串成"自动修复"，风险高于不修。
3. **现有读取器会隐藏重复，修复器不能用它做判据。**
   - `ParseNetworkObjectsDump` 对整行完全相同的重复 UUID 已折叠，并通过 `LastIdenticalDuplicateObjectCount` 报数量（`VeyonNetworkObjectDirectory.cs:428-431,486`）。
   - `ParseConfigurationDirectory` 内部用 `identities` 字典（`:1160`）同样折叠相同条目。
   - 所以修复器必须读 `config export` 的**未折叠 JsonStoreArray**，看到的是"物理条目"，而不是解析后的对象集合。

### B.2 第一阶段（随 S2/S3 一起上，只读）

检测到同 UUID 物理重复时：

1. 标记 `DuplicateDirectoryObject`，**该 Location 范围内禁止 add/remove**；其他地点不受影响。
2. 生成诊断包：`config export` 原文（脱敏后）、重复条目的原始序号与字段、`dump` 结果、当前指纹，存到 `VeyonCampus/Teacher/diagnostics/duplicate-{utc}/`。
3. 自动生成一份**修复预览**（不执行）：保留哪条、删除哪条、删除后预期对象数与字段差异。
4. 界面只显示一句话和一个"查看详情"，**不提供任何执行按钮**。

### B.3 第二阶段（独立高级维护功能，默认关闭）

前置条件全部满足才允许执行：

- 修复前重新 `config export`，计算指纹，与预览时的指纹比对；不一致即中止，禁止用过期预览执行覆盖。
- 逐字段比对：全部对象 UUID 集合、每台电脑的父地点 UUID、Host、Name、对象总数、`BuiltinDirectory` 下其他顶层字段。**不接受"电脑数相同"作为未受影响证据。**
- 备份写入独立目录（不得放在会被正常 Load 当成活动数据的位置），并记录源文件 SHA-256。
- 执行一次导入；随即 `dump` 读回。读回失败或结果与预期不符 → **停止自动恢复**，保留两份文件与诊断，提示人工恢复；不得自动再写第二次。
- 只在隔离 Windows VM（Veyon 4.11.2 实机）验证通过后才考虑开放"一键修复"。

### B.4 明确不做

- 不在检测到重复时自动修复。
- 不在导入失败后自动重试或自动回滚覆盖。
- 不把"折叠后的对象集合"当作修复前后对比的基线。

---

## C. P-05（重写）：不是加一行赋值，是重建地点读取模型

第 1 版要求 `VeyonLocationObjectId = location.ObjectId`。该代码**无法编译**，因为类型里没有这个属性。更关键的是：即使加上属性，当前数据源也拿不到 UUID。

### C.1 五条硬事实

| # | 事实 | 证据 |
| --- | --- | --- |
| 1 | `VeyonNetworkLocation` 只有 `Name / Targets / TargetDisplayNames`，无 UUID；注释明写"Windows Veyon exports remain name-based" | `src/VeyonCampus.Core/VeyonNetworkLocation.cs:3-9` |
| 2 | `ReadLocations()` 的数据源是 `networkobjects export` 生成的 CSV（name/host/location 三列），**不包含任何 UUID** | `VeyonNetworkObjectDirectory.cs:112`（`ExportFormat`） |
| 3 | `ParseLocationsFromSnapshot` 按**地点名称**分组，同名地点被合并成一条；且 `Where(Targets.Count > 0)` 直接丢弃 0 台电脑的地点 | `:271-273`、`:290` |
| 4 | `VeyonLocationImportResult` 不含 LocationId，`AddLocation` 内部算出的 `locationId` 在返回时丢失 | `:28-31`；内部计算在 `:735-742` |
| 5 | 非 Windows 平台（macOS 原生目录）没有 Veyon Location UUID 概念，`ReadLocations` 直接 `PlatformNotSupportedException` | `:105-107` |

事实 3 同时解释了两件现场现象：同名地点为什么只显示一个、为什么"同一地点重复 Host"会抛异常（合并后两台电脑的 Host 撞在一起）。

### C.2 改动清单（S2 的真实工作量）

1. `VeyonNetworkLocation` 增加 `Guid? ObjectId`（可空）。非 Windows 原生目录保持 `null`，调用方不得要求非空。
2. 新增/改造地点读取：以 `ReadNetworkObjects()`（`dump`，带 `ObjectId` + `ParentObjectId`）为**权威来源**构造地点；`export` CSV 只用于与 `dump` 交叉核对指纹，不再作为地点清单的唯一来源。
3. `ParseLocationsFromSnapshot` 改为按 `ObjectId` 分组（UUID 缺失时才退回名称并标记不可绑定）；**同名不同 UUID 不得合并**。
4. **保留 0 台电脑的地点**：空地点也必须能被读出，否则"0 座位机房仍能确认地点"（v2.1 R13）永远无法满足。
5. `VeyonLocationImportResult` 增加 `Guid? LocationObjectId`，让首次建档在创建成功后立即拿到 UUID 并持久化。
6. 所有平台判断显式化：`ObjectId == null` 时界面显示"本平台无 Veyon 地点身份"，此时 Room 只能做本机使用，不得显示"地点未绑定"这种暗示可修复的文案。

### C.3 新增回归

- **R19**：两个同名 Location（UUID 不同）→ 读取结果为 **2 条**，各自携带 UUID，且不合并 Targets。
- **R20**：0 台电脑的 Location → 仍出现在读取结果中，且允许 Room 绑定该 UUID。
- **R21**：Room 已绑定 A 地点，目录中新增同名 B 地点 → 解析结果仍指向 A，不得按名称漂移。

---

## D. P-03（部分采纳）：静态守卫 + 行为测试，两者都要

评审意见成立：静态搜索证明不了"运行时按 UUID 选择"。调整如下：

1. **行为测试（主）**：R21 已在 C.3 给出——同名两地点场景下，Room 的目标集合必须来自其绑定 UUID 对应的那一个地点。这是真正的验收。
2. **静态守卫（辅）**：`grep` 断言 `DisplayName` 与 `location.Name` 同现次数为 0，用来防止后续新代码再加回按名查找；它只做回归护栏，不作为"已修复"的证据。
3. `MergeLocation` 标记 `[Obsolete]`，编译警告兜住新调用点。

---

## E. P-06（文案修正）：未绑定不等于缺电脑

第 1 版写的"1 个座位尚未在 Veyon 中 [立即新增]"是错的：座位不是 Veyon 对象，未绑定可能只是一个空座位。

改为中性、非催促的表述：

```
本机已保存 · 2 个座位未关联电脑   [绑定已有设备] [新增电脑]
```

规则：

- 默认只作为状态说明，**不当作错误、不阻塞保存**。
- 只有当该座位填了学生姓名、且用户此前明确点过"新增电脑"但未完成时，才升级为需要处理的提示（指向待恢复意图，见 v2.1 §7.4）。
- 空座位（无姓名、无编号、无绑定）不参与计数提示。

---

## F. 采纳不变的四项

- **P-02**：保留"删除学生电脑（含 Veyon）"，并保留"仅从 Veyon 删除、座位转 Unbound"作为高级项（理由与第 1 版相同：换机与退役是两种合法诉求，且该功能是用户点名要求过的）。
- **P-04**：S2 出内部测试包，先验证 UUID 持久化：关闭重开地点仍在、改 Room 名不影响对应、空座位图仍显示已绑定地点。
- **P-07**：用真实档案做迁移夹具，并加"Room 名 ≠ Location 名"的历史变体（`R18`）。
- 前一轮的 C-01/C-02/C-04/C-05 事实纠正继续有效。

---

## G. S2 范围修订（因 P-05 变大）

原 v2.1 的 S2 只写了"Room 增 UUID + schema 3 + 改关键读取路径"。加上 P-05 之后，S2 实际包含三块，建议拆成三个提交而非一个：

| 提交 | 内容 | 可验证点 |
| --- | --- | --- |
| S2-a | `VeyonNetworkLocation` 加 `ObjectId`；地点改由 `dump` 构造；不合并同名、不丢空地点；`VeyonLocationImportResult` 暴露 LocationId | R19 / R20（此时尚未落盘，纯读） |
| S2-b | `TeacherRoomProfile` 加 `VeyonLocationObjectId`；schema 2→3；备份与迁移；首次建档写 UUID | 关闭重开地点仍在；R18 |
| S2-c | 15 处按名查找改按 UUID；`MergeLocation` 降级为导入候选 | R21；grep 守卫为 0 |

S2-a 是纯读改造、零迁移风险，可先单独提交并跑绿；S2-b 涉及数据写入，必须带备份；S2-c 是机械替换但面广，需要逐处确认不是"看起来一样其实语义不同"。

---

## H. 版本处置

- 以 v2.1 正文 + 本文件第 2 版为实施依据。
- 第 1 版的 P-01、P-05 作废；P-03、P-06 以本文件为准。
- v2.1 的 S0–S5 顺序与不变量集合不变；S2 内部拆为 a/b/c 三段。
