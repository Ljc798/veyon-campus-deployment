# 地点 / Veyon 目录同步 · 2026-11-04 现场反馈与问题清单

> 来源：用户 4 张界面截图（教师端「地点与学生名单 / 学生名单」+ Veyon 配置器 4.11.2「地点及计算机」），以及 2026-11-04 18:25 的补充反馈。
> 测试对象：[TeacherConsole 1.1.56 座位编辑测试包](2026-11-04-device-directory-sync-manual-checklist.md)，SHA-256 `576F06EC5977537CB377341166F0530500D727166494B777982BE1F43F7D312B`。
> **证据级别**：截图与用户口述为 `FIELD-FAIL`（现场事实）；19:07 的 Veyon 配置导出 / `networkobjects dump` / CSV / 本机档案为 `FIELD-EVIDENCE`（**本机只读取证，未写入**）；代码归因为源码可定位事实或 `CODE-SUSPECT`。
> **本记录使用 L01–L08 局部编号，不并入 Maintenance 问题台账 001–012**；跨文档引用写 `L0x`。
> **2026-11-04 18:25 修订**：纠正上一版把图1 误判为「名单为空」的结论；补记 L07（删一个、两个都消失）；记录本轮已实施的代码改动与用户决策。
> **2026-11-04 18:52 再修订**：用户明确「删除发生在 Veyon Configurator 内，两个同名地点里的电脑信息一模一样，删一个两个同时消失」。据此更正 L07 的归因——**不是教师端档案级联删除**，而是 Veyon 目录里同一对象被重复引用；同时把图1 的目录读取失败与图4 的重复地点串成同一条因果链（见 L07-a）。
> **2026-11-04 19:07 三修订**：已在本机执行**只读导出取证**（见 F 节）。L07-a 的推断获得实机证据坐实并精确化为「配置数组里同一对象被物理写入两条」；补记 L08（同名保护挂在已合并的 CSV 上，本机不会触发）。**本轮未写入 Veyon、未清理任何对象、未运行测试/构建。**
> **2026-11-04 20:04 四修订**：用户澄清「删除时没有保存并重置了」，两条重复条目仍在属正常，疑点闭环；已实施 P0-4（完全相同重复对象安全去重 + 非阻断提示，见 G 节），并完成手机端「学生电脑」页完善。

---

## A. 逐条反馈 → 结构化问题

| ID | 用户原话 / 截图 | 结论分类 | 当前状态（19:07 取证后） |
| -- | --------------- | -------- | ----------------- |
| L01 | 图1：目录读取失败、已改用可信配置快照；同一段提示重复出现 | 回退态保护生效，但**诊断不可操作、重复三遍** | 已改代码（诊断结构化 + 去重），待 VM 验证 |
| L02 | 图1：地点名单可显示但 UUID 设备目录未能读回 | 地点与设备可信级别未分开表达 | 已改文案，待 VM 验证 |
| L03 | 图1：不能删除学生座位，不知道这些作用 | 产品语义与用户预期错位 | **用户决定 D-03 先不做**，仅保留说明 |
| L04 | 图2 + 补充：名单不是空的，而是**莫名其妙多出来一个校区**，Veyon 里也多出来一个 | 本机档案自动生成（教师端）+ Veyon 配置数组重复条目（Veyon 侧） | **已实证**：本机档案 18:02:53 生成两份；已改代码（仅首次安装允许创建） |
| L05 | 图3：「当前地点设置」有和没有一样，可以删掉 | 遗留第二数据源 | **用户已确认删除，面板已移除** |
| L06 | 图4：Veyon 里两个同名「测试校区」，新增校区没同步 Veyon | Veyon `JsonStoreArray` 里同一 Uid 的 Location 重复 2 条 | **已实证**；现有同名保护不会触发（L08），清理工具待做 |
| L07 | 补充：**删掉了一个，两个都删掉了** | Veyon 配置数组同一对象重复 2 条，Configurator 两行共享同一 UUID | **已实证**，与教师端档案删除无关 |
| L08 | 19:07 取证发现（非截图） | 「同名地点拒绝自动应用」检查的是已按名字合并的 CSV，重复 Uid 脏数据不会触发该保护 | 新增 P0-4 待修；当前靠 `PartialRead` 护栏兜底 |

### L01 · 目录只读回退的提示（结论已纠正）

**用户明确纠正**：「名单并不是空的，而是莫名其妙多出来了一个校区，而且 Veyon 中也多出来了一个。」上一版把图1 判断为「学生名单为空」是**误判**——截图里的「尚无可选的学生电脑」只是不完整核对态下某个区域的局部文案，不代表整份名单为空，也不代表数据丢失。真实异常是**多出校区**（见 L04）和**删除联动**（见 L07）。

**截图仍然成立的部分**：`Veyon 当前名单尚未通过完整核对；已隐藏未验证的电脑`、`目录只读回退提示：Veyon 电脑目录清单读取失败，已改用可信配置快照（5f5cb77297da）`、`Veyon 地点名单可显示，但 UUID 设备目录未能读回`，且同一段提示在页面里出现三遍。

**代码定位**

- `src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs:789–814`：`networkobjects dump` 失败后尝试 `config export` 快照，返回 `PartialRead`，`LastTrustedDirectoryFingerprint = null`。
- `TeacherViewModel.RejectUnverifiedWindowsDirectoryRoster()`：把同一段 message 同时写入 `WebsiteDirectoryError` 与 `ClassroomDirectoryError`，`ReadWebsiteLocationsAsync` / `ReadClassroomLocationsAsync` 的 `finally` 又各追加一次 `GetVeyonDirectoryReadNotice()` → 同屏重复三遍。另有一处在身份核对分支里先写完整 notice 再 append 到状态行。

**本轮已改**

1. `GetVeyonDirectoryReadNotice()` 重写：不再重复拼接 `diagnostic.Message` 里已包含的原始错误，改为结构化字段（只读快照指纹、最近成功读取、CLI 路径、失败命令、退出码、是否超时、截断后的原始错误）＋明确下一步「核对 Veyon CLI 路径与管理员权限后点击重新读取目录」，并写明「修复前不会下发策略、写入 Veyon 或绑定 UUID」。
2. 完整诊断只在「地点与学生名单」页展示一次；策略页改为一句指向性短文案。
3. 三处 `finally` / 状态行追加点增加 `VeyonReadNoticeMarker`、`VeyonNoticePointer` 去重判断，同屏不再出现三遍。
4. 去掉「重新进入本页重试」这种没有操作价值的措辞。

**仍待办**：提供「导出目录诊断」按钮（CLI、退出码、脱敏 stderr 落盘不上传）；取实机日志确认 `dump` 失败原因后再决定是否改读取链——**拿不到日志前不放宽任何校验**。

### L02 · 地点与设备的可信级别

截图「Veyon 地点名单可显示，但 UUID 设备目录未能读回」是 `PartialRead` 的正常表现：地点名来自配置快照，UUID 与电脑对象不可信，教师端因此拒绝把它们作为课堂范围。旧文案把两件事混成一句「尚未通过完整核对」。本轮已在 L01 的新诊断里拆开写清「地点名可显示，但设备 UUID 不可信，已隐藏未验证的电脑」。地点列表逐项置灰 / 标注可信级别仍待 UI 实现。

### L03 · 删除座位语义（D-03 暂不做）

- `TeacherRoomView.axaml` 右键菜单「删除此座位」「解除本机电脑关联」仅在 `IsSeatMapEditing == true` 出现；删除座位**不删除 Veyon 对象**（[验收清单 A2](2026-11-04-device-directory-sync-manual-checklist.md)）。
- 用户想删的是「学生/电脑」，产品只提供删本机座位，属于语义错位。
- **用户 18:25 决定：D-03 先不做**，本轮不新增「从 Veyon 移除学生/电脑对象」能力，也不改动 Veyon 删除语义。后续如要做，必须备份 + 二次确认 + 按 UUID + 读回，且禁止按显示名删除。

### L04 · 凭空多出一个校区（根因已定位并修复）

**代码定位（已确认，不只是推测）**

- `TeacherViewModel.ApplyClassroomVeyonLocation()`：读到一个 Veyon 地点后调用 `TeacherCampusProfileFromVeyon.MergeLocation(campus, campusName, location)`。当 `campus` 为 `null`（`SelectedCampusProfile` 为空、且 `FindSavedSeatMapCampus` 找不到可确认档案、本机档案数不为 1）时，`MergeLocation` 会执行 `new TeacherCampusProfile(Guid.NewGuid(), campusName, [])` **新建一份校区档案并持久化**。
- `campusName` 依次取 `RoomCampusName` / `WebsiteSigningCampuses[0]` / `CampusId` / 「本机校区」，因此档案名可能来自配置包或签名校区名，而不是用户主动输入。
- `MergeLocation` 内部用 `rooms.FindIndex(room => room.DisplayName == location.Name, OrdinalIgnoreCase)` **按地点显示名复用 RoomId**；两个 Veyon 同名地点会被合并成同一份本机地点。
- `ApplyClassroomVeyonLocation` 保存时 `CampusProfiles.Where(item => item.ProfileId != mergedCampus.ProfileId)` 再 `Add(mergedCampus)`，即按 ID 整体替换档案。

**结论**：「莫名其妙多出来一个校区」有明确代码路径——**一次 Veyon 目录读回就可能凭空落一份本机档案**。仓库源码里没有「测试」「测试校区」字面量，所以它们来自教师机本机档案；具体是哪次操作生成仍需档案文件时间戳确认，但自动生成机制已被证实。

**本轮已改**

1. `ApplyClassroomVeyonLocation`：本机已有档案且无法确认归属时，**只读取、不写入**，给出明确错误「已读到 Veyon 地点 X，但本机已有 N 份校区档案，无法确认它属于哪一份…请在地点与学生名单选择对应档案，或先清理不需要的档案，再重新读取目录」。
2. 仅当 `CampusProfiles.Count == 0`（真正的首次安装）才允许创建一份，并在状态栏明确写「首次读取：已为本机创建校区档案…」。
3. 档案下拉改为**常显**（只要有档案），并在下方说明「从 Veyon 读取地点不会自动新建档案；删除校区档案会同时删除其下所有地点，但不会修改 Veyon」。
4. 删除确认框动态列出将被删除的校区名、其下全部地点名与数量，删除后状态栏写明删除了几个地点、剩余几份档案、Veyon 未更改。

**19:07 取证**：本机 `C://Users//Administrator//AppData//Local//VeyonCampus//Teacher//campus-directory.json`（1328 B，创建/修改均为 2026-11-04 18:02:53）已读出 **两份**校区档案：「测试」（room「测试校区」，PC-02–PC-06 / 5 台）与「测试校区」（room「测试校区」，PC-01–PC-05 / 5 台）。**图2 的两个校区来自这里，不是 Veyon**；两份模板的电脑编号与 Veyon 实际 4 台电脑（`desktop-fgvqhia` / `pc-02` / `PC-07` / `PC-08`）都不一致，进一步印证模板是误导性的第二数据源。

**仍待办**：档案下拉显示创建时间/来源（需扩展持久化 schema，本轮未改）；这两份测试档案是否清理需单独授权，清理前先备份。

### L05 · 「当前地点设置」面板（已确认删除并实施）

`SaveRoomProfile()` 只调用 `PersistCampusDirectory`，提示「机房模板已保存」，**完全不写 Veyon**；`DeleteSelectedRoomProfile()` 也明确「Veyon 地点与电脑目录未更改」。这套前缀/起始编号/数量的机房模板在 1.1.56 改为以 Veyon 实际名单为准后退化为第二数据源，正是「保存后没同步」「有和没有一样」的来源。

**用户已确认删除**。本轮已移除：

- `TeacherRoomView.axaml` 的整块「当前地点设置」卡片（地点名称 / 前缀 / 起始编号 / 电脑数量 / 保存地点设置按钮）；
- `TeacherRoomView.axaml.cs` 的 `SaveRoomProfileRequested` 事件与 `RequestSaveRoomProfile`；
- `TeacherWindow.axaml` 的事件绑定与 `TeacherWindow.axaml.cs` 的 `SaveRoomProfile` handler。

**保留**：ViewModel 里的 `SaveRoomProfile()` / `DeleteSelectedRoomProfile()` 及旧档案字段的读取兼容（不破坏已有 `campus-directory.json`），以及座位图与 Veyon 同步流程。原来显示在面板里的 `CampusDirectoryStatus` 移到档案下拉卡片下方，操作反馈不丢失。

### L06 · Veyon 两个同名「测试校区」

`BuildImportPreview` 已检测同名地点并阻止写入，`AddLocation` 仅在 `!preview.LocationExists` 时创建地点，写后读回还要求同名地点 UUID 唯一——所以「刚添加校区没同步 Veyon」是**保护生效**，不是同步逻辑漏了。问题在于教师端读取路径此前没有任何同名检测，且 `SelectedRoomProfile` 按 `location.Name` 字符串匹配，两个同名地点无法区分。

**本轮已改**：新增 `FindDuplicateVeyonLocationName()` 与 `RejectDuplicateVeyonLocations()`，在 `ReadWebsiteLocationsAsync` / `ReadClassroomLocationsAsync` 拿到地点列表后先检测同名；命中则清空课堂/策略目标并给出醒目错误「Veyon 中存在多个同名地点 X…已停止自动应用，未修改任何本机档案、学生姓名或 Veyon 配置。请在 Veyon 配置器中清理或重命名重复地点（先导出配置备份），再点击重新读取目录」，**不再静默合并座位图**。

**仍待办**：一次性清理/合并工具（备份 → 列出两个地点 UUID 及电脑 → 人工选择 → 按 UUID 迁移/删除 → 读回）；长期改为地点 UUID + 设备 UUID 绑定，显示名仅作展示。

**19:07 取证发现（L08）**：`RejectDuplicateVeyonLocations` 检查的是 `ReadLocations` 返回的列表，而该列表来自 `networkobjects export` 的 CSV；实测 CSV 只有 4 行、地点名全部「测试校区」，两条重复 Location 已被 `ParseLocationsExport` 按名字合并成 **1 个**地点，所以本机场景下该保护**不会触发**。真正阻止误用的是 `RejectUnverifiedWindowsDirectoryRoster()`（dump 失败 → `PartialRead`）。保护挂在错误数据源上，见 C 表 P0-4。

### L07 · 删掉一个，两个都删掉了（新补充）

**用户原话**：「名单并不是空的，而是莫名其妙多出来了一个校区，而且 veyon 中也多出来了一个，但是奇怪的是我删掉了一个，两个都删掉了。真不知道为什么。」

**已确认的代码行为**

- `DeleteSelectedCampusProfile()` 按 `ProfileId` 过滤，**删除的是整份校区档案，连同其下全部 `Rooms`**；`DeleteLayout` 还会清理对应座位图。因此一份档案下挂两个地点时，删除一次会同时失去两个地点。
- `MergeLocation` 按地点显示名复用 `RoomId`（见 L04），使「两个地点」可能被折叠进同一份档案/同一个 RoomId。
- `DeleteSelectedRoomProfile()` 与 `DeleteSelectedCampusProfile()` 都**只写本机**，确认框与状态文案均声明「Veyon 地点与电脑目录未更改」；`networkobjects clear` 只在卸载/配置重置路径（`VeyonCampusConfigurationReset`）调用，与档案删除无关。

**判断（18:52 更正）**：用户已明确是在 **Veyon Configurator 的地点列表里删除**的。教师端没有任何删除 Veyon 地点的代码路径（唯一的 `networkobjects clear` 只在卸载 / 配置重置时调用），所以「删一个两个都消失」**不是教师端档案级联删除造成的**——上一版把它归到 `DeleteSelectedCampusProfile` 是错误归因，特此更正。两个显示项电脑信息完全一致、删一个同时消失，指向 **Veyon 目录里同一个底层对象被引用 / 列出了两次**，而不是两份独立数据。详见 L07-a。

**本轮已改（让级联可知、可确认）**

1. 删除确认框列出档案名、将删除的地点名（最多 10 个）与总数，并明确「只删教师电脑上的档案、学生姓名与座位图，不会修改或删除 Veyon 中的地点与电脑」。
2. 删除后状态栏写明「已删除校区档案 X 及其 N 个地点；Veyon 未更改；本机还剩 M 份档案，请重新读取 Veyon 目录后再确认课堂范围」。
3. 配合 L04（不再自动建档）与 L06（同名地点拒绝自动应用），避免两个地点被静默合并成一份，再被一次删除带走。

### L07-a · 18:52 补充：为什么删一个会两个都消失，和图1 是同一件事

**用户新增事实**：在 Veyon Configurator 的「地点及计算机」里操作；两个同名地点**里面的电脑信息一模一样**；删掉其中一个，**两个同时被删除**。

**代码级解释（已确认）**

1. `VeyonNetworkLocation` 只有 `Name` / `Targets`，**没有 UUID**；源码注释明确写着 `Windows Veyon exports remain name-based`。教师端读回地点靠 `networkobjects export` 的 CSV（`ParseLocationsExport` 用 `Dictionary<string, ...>(OrdinalIgnoreCase)` **按地点名分组**），两个同名地点会被合并成一条；如果两处电脑 Host 相同，还会直接抛「导出包含重复 Host/电脑目标」使读取失败。
2. `ParseNetworkObjectsDump`（`networkobjects dump` 的带边框表格）用 `HashSet<Guid> objectIds` 校验对象 UUID，**遇到重复 UUID 就抛 `InvalidDataException`**。
3. 该异常被 `TryReadNetworkObjectsDump` 捕获为 `ParseFailed`，`SafeReadNetworkObjects` 随即回退到 `config export` 快照并标记 `PartialRead`，`LastTrustedDirectoryFingerprint = null` —— **这正是图1「Veyon 电脑目录清单读取失败，已改用可信配置快照（5f5cb77297da）」和「UUID 设备目录未能读回」的来源**。上一版把它当成独立的 CLI 失败，实际很可能就是重复地点对象造成的。
4. 教师端随后 `RejectUnverifiedWindowsDirectoryRoster()` 隐藏未验证电脑；`RejectDuplicateVeyonLocations()`（本轮新增）再阻止按名字自动应用，避免把重复地点折进同一份座位图。

**结论**：两个显示项共享同一个底层 Veyon 对象（同一 UUID 在目录中被引用两次），所以在 Configurator 里删掉该对象，两行会一起消失；电脑信息完全一样也是同一个原因。**这与教师端删除校区档案无关**，教师端没有任何删除 Veyon 地点对象的路径。

**19:07 实机实证（结论坐实并精确化）**：已在本机执行 `veyon-cli config export`（退出码 0）。`BuiltinDirectory/NetworkObjects/JsonStoreArray` 共 6 条，其中 **Type=2 的 Location 有 2 条，名称均「测试校区」、Uid 均 `{a285c66c-569b-5f3e-9880-5903b72943bf}`**；Type=3 的电脑 4 条（`小子`/`desktop-fgvqhia`、`123123`/`pc-02`、`333`/`PC-07`、`222`/`PC-08`）**ParentUid 全部指向该 UUID**。所以不是「引用重复」这种抽象情况，而是**配置数组里同一条地点对象被物理写入了两遍**（数组下标 0 与 3）。Configurator 按 parent 取子对象，两行自然显示同一批电脑；删除该 UUID 对象，两行一起消失。`networkobjects dump` 的表格同样把该 UUID 打印了两次 → `HashSet<Guid>` 抛错 → 图1 的回退提示。完整数据见 F 节。

**当前代码的护栏（复核）**：`ReadNetworkObjects` 在 `PartialRead` 时抛错而非返回回退对象，因此 `AddLocation` 不会在目录不完整时创建地点；`BuildImportPreview` 也会拒绝「多个同名地点」。**但护栏只是阻止写入，不会自愈已有脏数据**——只要这 2 条重复条目还在，教师端就会一直停在只读回退、隐藏未验证电脑，这正是用户遇到的「地点一直不好用」。重复条目由哪次操作写入（旧版本 / 配置导入 / 手工）仍无日志可判。**20:04 用户澄清：当时在 Veyon Configurator 里删除后没有保存并做了重置，所以两条仍在**——不是删除失效，也不是被重新导入，该疑点已闭环。

**18:52 已改的诊断**：`ParseNetworkObjectsDump` 与 `ParseLocationsExport` 的错误文案现在直接说明「两个同名地点、电脑完全一样、删一个两个一起消失，请先导出备份再核对，教师端不会自动删除或合并 Veyon 对象」；`RejectDuplicateVeyonLocations` 的提示补充了同一判断与「不要在未备份时直接删除」；教师端回退诊断的原始错误截断从 300 放宽到 600 字符。

**19:07 补充**：`ParseNetworkObjectsDump` 的重复 UUID 异常现在带上**实际 UUID、类型与对象名**，并明确指出去 `config export` 的 `JsonStoreArray` 核对，便于现场一次定位，不再只给通用描述。

---

## B. 根因归纳（修订）

一句话（19:07 实证修订）：**Veyon 配置 `BuiltinDirectory/NetworkObjects/JsonStoreArray` 里同一条 Location 对象（同一 Uid `a285c66c…`）被物理写入了两次，Configurator 因而显示两个同名、电脑完全一样的地点，删一个两个一起消失；`networkobjects dump` 解析因「重复 UUID」失败 → 回退 `config export` 只读快照 → 教师端进入 `PartialRead` 并隐藏未验证电脑。叠加教师端全程按地点名而非 UUID 处理、Veyon 地点读回会自动落一份本机档案（本机 18:02:53 生成「测试」「测试校区」两份），于是同时出现「目录读取失败」「凭空多一个校区」「同名地点」「删一个没两个」。**

四个叠加因素：

1. **读回即写入**：`ApplyClassroomVeyonLocation` 在找不到档案时 `MergeLocation(null, ...)` 新建档案（L04）。
2. **按显示名合并**：`MergeLocation` 按名称复用 RoomId；读取/选择路径按 `location.Name` 字符串匹配（L06、L07）。
3. **删除是档案级级联**：按 ProfileId 整份删除，Rooms 与座位图一并清理，但旧 UI 只说「删除校区档案及其机房档案」，没有列出会失去什么（L07）。
4. **双数据源**：机房模板只写本机、不写 Veyon，用户以为保存即同步（L05，已删面板）。

## C. 处理顺序与进度

| 阶段 | 内容 | 状态 |
| ---- | ---- | ---- |
| P0-1 | 实机取证：Veyon CLI 退出码/stderr、本机档案文件与时间戳、Veyon 配置导出、两个同名地点 UUID、删除操作顺序 | **已完成（19:07 本机只读导出，见 F 节）**；写入 Veyon 的清理动作仍需授权 |
| P0-2 | 修诊断重复与不可操作、明确「未写入任何内容」 | **已改代码**（L01/L02/L06），待 VM 验证 |
| P0-3 | 不再自动新建档案；同名地点拒绝自动应用；删除级联可见可确认 | **已改代码**（L04/L06/L07），待 VM 验证 |
| P1-1 | 删除「当前地点设置」面板 | **已完成**（L05，用户已确认） |
| P1-2 | 档案来源/创建时间展示、删除档案入口说明、座位删除语义拆分 | 部分完成（删除入口说明已做）；来源/时间需改 schema，**D-03 不做** |
| P0-4 | 修正 L08：让完整 UUID 目录读取与 `config export` 一致地处理重复对象，并把重复状态显式告知教师端 / 手机端 | **已改代码（20:04，见 G 节）**，待新测试包 VM 回归 |
| P1-3 | 同名地点清理/合并工具（备份 + 按 UUID + 人工选择 + 读回） | 未开始；仅在可还原环境、用户授权后 |
| P2 | 地点与设备全面改为 UUID 绑定，显示名仅作展示 | 未开始；依赖 P1-3 与回归验收 |

## D. 需要用户确认 / 提供的材料

- [x] ~~是否同意删除「当前地点设置 / 机房模板」面板~~ → **已确认删除，已实施**。
- [x] ~~是否需要「从 Veyon 移除学生/电脑对象」~~ → **先不做（D-03）**，本轮未新增该能力。
- [x] ~~当时的删除操作顺序~~ → **已明确：在 Veyon Configurator 的地点列表中删除**；两个同名地点的电脑信息完全一致，删一个两个同时消失。
- [x] ~~两个同名地点的 UUID 是否相同、`parent` / `children` 关系~~ → **19:07 已确认：两条 Location 的 Uid 完全相同**（`{a285c66c-…}`），4 台电脑 `ParentUid` 全部指向它；是配置数组里的重复条目，不是两个独立对象。
- [x] ~~实机证据：CLI 退出码、CLI 路径、本机档案及时间戳、Veyon 版本、配置导出~~ → **已完成**，见 F 节；三个命令退出码均为 0，Veyon 4.11.2。
- [x] ~~删除后重复条目为何仍在~~ → **20:04 已澄清：删除未保存并重置，属正常**；删除前无历史配置备份，19:07 的 `config export` 已作为当前基线备份。
- [ ] **待授权**：是否清理 Veyon 里这 2 条重复 Location（保留 1 条）？是否需要清理本机 `campus-directory.json` 里的「测试」「测试校区」两份测试档案？两者都是写/删操作，须先备份 + 明确确认，本轮未执行。
- [ ] 统一测试排期：本轮**未运行测试/构建/打包**，改动需要新测试包 + Windows VM 回归后才算验证。

## E. 本轮代码改动清单（2026-11-04 18:25 后）

| 文件 | 改动 |
| ---- | ---- |
| `src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml` | 删除「当前地点设置」整块卡片；档案下拉常显并加来源/删除说明与状态行 |
| `src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml.cs` | 移除 `SaveRoomProfileRequested` 事件与 `RequestSaveRoomProfile` |
| `src/VeyonCampus.App/TeacherWindow.axaml` / `.axaml.cs` | 移除保存地点设置事件接线与 handler；删除校区档案确认框改为动态列出地点与影响范围 |
| `src/VeyonCampus.App/TeacherViewModel.cs` | 诊断结构化与去重（`VeyonReadNoticeMarker` / `VeyonNoticePointer`）；新增同名地点检测 `RejectDuplicateVeyonLocations`；`ApplyClassroomVeyonLocation` 仅在首次安装允许创建档案；删除档案后明确反馈；18:52 补充重复 UUID 说明、原始错误截断放宽到 600 字符 |
| `src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs` | `ParseNetworkObjectsDump` 把「重复 UUID」单独报错并说明「两个同名地点、电脑完全一样、删一个两个一起消失」及先备份再处理；`ParseLocationsExport` 的重复 Host 错误补充同一解释；**19:07** 重复 UUID 异常补上实际 UUID / 类型 / 名称与「核对 `JsonStoreArray`」的指向 |
| `src/VeyonCampus.Core/VeyonNetworkObjectDirectory.cs`（20:04） | `ParseNetworkObjectsDump` 与 `ParseConfigurationDirectory` 语义对齐：**完全相同且同 Uid 的重复行安全去重**（新增 `LastIdenticalDuplicateObjectCount`），同 Uid 内容不一致仍拒绝读取 |
| `src/VeyonCampus.App/TeacherViewModel.cs`（20:04） | `GetVeyonDuplicateObjectNotice` / `AppendVeyonDuplicateObjectNotice`：两个读取入口追加一条非阻断重复对象提示（带 marker 去重）；单地点读取成功后状态行写明机房数 / 电脑数，不再停在「正在读取…」 |
| `src/VeyonCampus.App/MobileWeb/`（20:04） | 手机端「学生电脑」页新增目录状态卡（三态），收敛三处重复提示并对齐桌面端口径；刷新按钮加文字与失败反馈；静态资源版本 v56 → v57 |

**未做的事**：未运行测试、构建或打包（沿用「测试统一安排」约束）；未写入 Veyon 目录、未触碰 production、未改持久化 schema、未新增 D-03 能力。静态检查：两个 XAML 可解析、C# 大括号平衡、115 个 TeacherWindow 事件绑定全部有对应 handler、`cr-at-eol` 下 `git diff --check` 干净。

## F. 实机取证（2026-11-04 19:07，本机只读导出）

**执行人/环境**：本机 `Administrator`，管理员权限；`C://Program Files\Veyon\veyon-cli.exe`，Veyon **4.11.2**；`veyon-service` / `veyon-server` 正在运行。
**边界**：只执行 `config export` / `networkobjects dump` / `networkobjects export` 三条只读命令，**没有执行 `config import`、`networkobjects add/import/clear`，没有删除或合并任何 Veyon 对象，没有运行测试/构建/打包**。

| 命令 | 退出码 | 产物 |
| ---- | ------ | ---- |
| `veyon-cli.exe config export <file>` | 0 | `artifacts/veyon-evidence-20261104-1907/veyon-config-export.json`（3912 B） |
| `veyon-cli.exe networkobjects dump` | 0 | `networkobjects-dump.txt`（1350 B，10 行表格） |
| `veyon-cli.exe networkobjects export <csv> format %name%␟%host%␟%location%` | 0（`[OK]`） | `networkobjects-export.csv`（112 B） |
| — | — | `commands.log`（命令、退出码、时间戳） |

该目录位于仓库 `artifacts/` 下，已被 `.gitignore` 的 `/artifacts/` 忽略，**不会进入版本库**；文件含本机电脑名与地点数据，对外分享前请脱敏。

**配置结论（决定性证据）**

`BuiltinDirectory/NetworkObjects/JsonStoreArray` 共 **6** 条：

| 下标 | Type | Name | Uid | Host / ParentUid |
| ---- | ---- | ---- | --- | ---------------- |
| 0 | 2 Location | 测试校区 | `{a285c66c-569b-5f3e-9880-5903b72943bf}` | — |
| 1 | 3 Computer | 小子 | `{343a8e07-…}` | `desktop-fgvqhia` → `a285c66c…` |
| 2 | 3 Computer | 123123 | `{e406cc08-…}` | `pc-02` → `a285c66c…` |
| 3 | 2 Location | 测试校区 | `{a285c66c-569b-5f3e-9880-5903b72943bf}` | — （**与下标 0 完全相同**） |
| 4 | 3 Computer | 333 | `{8019dfa9-…}` | `PC-07` → `a285c66c…` |
| 5 | 3 Computer | 222 | `{40129c6d-…}` | `PC-08` → `a285c66c…` |

- 重复 Uid 统计：`{a285c66c-…}` 出现 **2** 次，其余各 1 次。
- `networkobjects dump` 表格同样把该 UUID 打印两遍 → `ParseNetworkObjectsDump` 的 `HashSet<Guid>` 抛 `InvalidDataException` → `SafeReadNetworkObjects` 回退 `config export` 并标记 `PartialRead` → 图1 提示。
- `networkobjects export` CSV 为 4 行、地点名全部「测试校区」、**无重复 Host**：`ParseLocationsExport` 按名字合并为 1 个地点、4 台电脑，因此教师端地点列表只有一个名字，`RejectDuplicateVeyonLocations` 不会触发（L08）。
- 本机档案 `C://Users//Administrator//AppData//Local//VeyonCampus//Teacher//campus-directory.json`：1328 B，创建/修改同为 **2026-11-04 18:02:53**；两份校区「测试」与「测试校区」，各含一个同名 room「测试校区」，电脑范围分别是 PC-02–PC-06 / PC-01–PC-05（各 5 台），与 Veyon 实际 4 台完全不一致。

**与四张截图的对应**：图1 = 重复 UUID 导致 dump 解析失败 + 回退提示；图2 = 本机档案两份校区（不是 Veyon）；图3 = 模板只写本机（已删面板）；图4 = Veyon 数组两条相同 Location；「删一个两个都消失」= 两行共享同一 UUID。

**建议的清理路径（未执行，需授权）**：以本次 `veyon-config-export.json` 为基线备份 → 在 Veyon Configurator 中按 UUID 确认并只保留一条 Location → `config export` 读回验证 `JsonStoreArray` 中无重复 Uid → 再在教师端「重新读取目录」。本机两份测试档案如需删除，另行备份后按档案删除流程处理。**在此之前不要直接删除地点**（会同时带走 4 台电脑的关联）。

**20:04 说明**：用户确认当时删除未保存并重置，所以两条重复条目仍在属正常；且 P0-4 已让教师端在这种脏数据下可用，**清理不再是恢复使用的前置条件**，可按维护窗口另行安排（仍需用户授权）。

---

## G. 20:04 后续实施（P0-4 + 手机端页面）

### P0-4：让脏数据不再把教师端锁死

依据：`ParseConfigurationDirectory`（`config export` 路径，1119–1125 行）本来就对「同 Uid 且内容完全相同」的条目 `continue` 去重，只有内容不一致才抛错；唯独 `ParseNetworkObjectsDump` 用 `HashSet<Guid>` 一律抛错。**修复不是放宽校验，而是让两条读取路径语义一致。**

- `ParseNetworkObjectsDump` 改为 `Dictionary<Guid, string>`（value = 除 UUID 外所有列的身份串）：完全相同 → 折叠并计数；同 Uid 但内容不一致 → 仍抛 `InvalidDataException`，文案带实际 UUID / 类型 / 名称并指向 `BuiltinDirectory/NetworkObjects/JsonStoreArray`。
- 新增 `LastIdenticalDuplicateObjectCount`（`volatile` 静态），供 UI 显示非阻断提示。
- **现场数据验证**：19:07 导出的 dump 中两条 Location 行的 Parent / Type / Name / Host / Mac **完全一致**，将被折叠 1 条 → `ReadNetworkObjects` 成功 → `RefreshVeyonDeviceDirectorySummaryAsync` 不再把 `IsVeyonDeviceDirectoryCurrent` 置 false → `RejectUnverifiedWindowsDirectoryRoster()` 不再隐藏电脑。**即不清理 Veyon，名单也能恢复可用**（L08 的保护数据源错配由「不再阻断 + 显式提示」化解）。
- `TeacherViewModel` 新增 `GetVeyonDuplicateObjectNotice()` / `AppendVeyonDuplicateObjectNotice()`：两个读取入口的 `finally` 各追加一次提示（带 marker 去重，不再出现三遍），说明「已按 UUID 安全去重、建议导出备份后核对清理、教师端不会自动删除或合并 Veyon 对象」。
- 顺带修：单地点读取成功后状态行停留在「正在读取…」的问题，改为「已读取到 1 个机房、N 台电脑；地点与 UUID 目录已核对。」

### 手机端「学生电脑」页完善

- 新增始终可见的 `#directory-health` 状态卡（`index.html`）：三态 `ok / warn / error`，显示地点数 / 电脑数、状态标签、主说明与可操作下一步，带 `role="status"` + `aria-live="polite"`。
- `app.js` 新增 `renderDirectoryHealth()`：按 `directoryLoadError`、`rooms`、同名地点、重复电脑计算状态；把原来散落在下拉、摘要、网格里的**三处重复提示收敛到一张卡**，文案与桌面端新口径对齐（先导出备份、按 UUID 核对、不要在未备份时直接删除、教师端不会自动删除或合并）。
- `roomSummary` 改为始终显示「当前教室：X · N 台电脑」，多地点时也能知道卡片属于哪个地点；下拉同名项标注「同名地点（已停用）」。
- 刷新按钮加「正在刷新…」文字与失败后的状态卡提示；`index.html` 静态资源 `?v=56 → 57`、`service-worker.js` `CACHE_VERSION v56 → v57`，避免 PWA 使用陈旧 shell。
- `styles.css` 新增 `.directory-health` 及三态配色，复用既有 `--teal / --amber / --red` 变量，与浅色主题一致。

**边界（21:29 更新）**：本轮代码已随 TeacherConsole 1.1.65 完成 Release 构建与离线打包（0 错误），但仍**未运行自动化测试 / 安装 / VM 验收**；未写入 Veyon、未清理重复条目、未删除本机测试档案、未触碰 production。安装包见 `artifacts/VeyonCampus-TeacherConsole-1.1.65-Maintenance-Test.exe`。静态检查：`node --check` 两个 JS 通过、HTML 标签配对、CSS 大括号平衡、C# 大括号平衡、127 个 `$()` id 全部存在、`cr-at-eol` 下 `git diff --check` 干净。手机端状态卡只消费现有 `/api/rooms` 数据，**不新增后端接口**；「教师端只读回退」在手机端只能按「名单为空 / 刷新失败」间接表达，若要精确展示需以后扩展 API。

---

## I. 22:00–22:22 复现、根因与现场清理（决定性）

**用户反馈**：1.1.65 装好后，Veyon 里又出现两个相同「测试校区」；本机档案下拉仍有「测试」「测试校区」两份；新加一个人无法同步，报错「添加结果需核对：地点操作已提交，但读回时地点对象 UUID 不唯一」。用户明确要求删掉多出来的一个，并要求能删除学生电脑。

**版本事实**：22:06:39 运行的进程是 `C://Program Files\Veyon Campus\Teacher\VeyonCampus.Teacher.exe`（1.1.65.0，注册表已有卸载项），此前 17:58 的 1.1.64 免安装进程已退出。所以 22:00 截图里的旧面板 / 重复诊断是旧版界面；但「重复地点又出现」是真的。

**决定性根因（Veyon 4.11.2 插件源码）**：`artifacts/veyon-4.11.2-BuiltinDirectoryPlugin.cpp` 的 `importFile()` 用 `objectManager.findByName(location)` 找父地点；查不到时执行 `parentLocation = NetworkObject(Type::Location, name); objectManager.update(parentLocation, true)` **追加一条 Location**。Location 的 UID 是按名字生成的（v5），因此追加条目与已有条目 **UUID 完全相同** → Configurator 显示两个同名、同电脑的地点，教师端 `dump` 解析又因重复 UUID 失败。教师端 `AddLocation` 原先调用 `networkobjects import <file> location <显示名> format ...`，每次新增电脑都会触发这个分支，所以「清理完再新增一个，重复又出现」。

**修复**：`VeyonNetworkObjectDirectory.AddLocation` 不再使用 `import`，改为逐台 `networkobjects add computer <name> <host> "" <父地点 UUID>`。`handle_add` 的 `findNetworkObject()` 支持按 UUID 定位父地点且**不会创建地点**；地点不存在时仍只 `add location` 一次并读回取 UUID。写入后原有的唯一 UUID 读回校验保留。

**现场清理（均已执行并读回验证）**：
- 22:05 第一次 `config import` 去重：6 条 → 5 条（1 Location + 4 电脑），其它配置键一字未改。
- 22:11 复现：用户装 1.1.65 并新增人员后，配置变成 8 条（2 条同 Uid Location + 6 台电脑，新增 PC-12 / PC-13）→ 坐实 import 是制造重复的写入路径。
- 22:15 第二次 `config import` 去重：8 条 → 7 条（1 Location + 6 电脑，**所有电脑对象保留**），`networkobjects dump` 只剩一个 Location。备份 / 前后导出都在 `artifacts/veyon-evidence-20261104-2200/`。
- 本机档案：备份 `campus-directory.json` / `classroom-seat-grid.json` 后删除冗余的「测试校区」档案（`ebc48f0a…`）及其 10 座位 layout，**保留用户当前正在编辑的「测试」档案（3 个座位）**。本机档案从 2 份变 1 份，Veyon 对象未被删除。
  - **22:40 更正：这一步删错了。** 被删掉的 `ebc48f0a…`「测试校区」档案才是与 Veyon 地点 UUID `a285c66c…` 真正对应的那份：它的 10 座位 layout 里 6 个座位带 `VeyonObjectId` / `VeyonLocationObjectId`，与 Veyon 实际电脑一一绑定；保留下来的 `84a192ac…`「测试」档案只有 3 个空座位、零条 Veyon 绑定，只是名字同为「测试校区」的误导性模板。已按 J 节恢复正确档案、移除错误档案。

**其它两项**：① 右键删除学生电脑已实现（见 G 节之后的本轮代码），预览模式也能弹出菜单；② 目录诊断文案压缩为一句，去掉快照指纹 / CLI / 退出码 / 原始错误堆叠。

**交付包**：`artifacts/VeyonCampus-TeacherConsole-1.1.67-Maintenance-Test.exe`（70,977,027 B，SHA-256 `8E5747C7E168B1B90D3B6A35E0C8604CBB5818DE4020EDF282395E995EA896F4`），Release 构建 0 错误、102 个唯一警告。**必须换到 1.1.67**（1.1.66 不含 import 修复）。

**边界**：自动化测试 / Windows VM 验收仍未运行；用户当前运行的是 1.1.65，改完本机档案后需重启到 1.1.67 才加载新数据。

---

## J. 22:40 现场复核：删错档案、地点没对应、右键无效、同步失败

**用户反馈**：截图显示「本机校区档案：测试 / 已保存的地点：测试校区 / 已添加 3 个座位 · 已填写 0 个姓名」，红字「本机资料已保存，但 Veyon 设备名单未能刷新；请检查读取权限后重试」，并指出「你删错了」「这个不是对于 Veyon 的校区地点，没有对应上」「右键删除学生无效」「无法同步到 Veyon」。

**取证**（`artifacts/veyon-evidence-20261104-2240/`，只读）：

| 项目 | 事实 |
| ---- | ---- |
| 已安装版本 | 注册表与进程均为 **1.1.67**，即上一轮交付包，不存在「装到旧版」问题 |
| 当前 Veyon 配置 | `config export` 退出码 0，`JsonStoreArray` 共 **3** 条：1 个 Location「测试校区」`a285c66c…` + 2 台电脑（`小子`/`desktop-fgvqhia`、`123123`/`pc-02`）。**地点重复已消失，Veyon 侧干净** |
| `networkobjects dump` | 顶层 Location 行 Parent UUID 为全零 `00000000-…`、Host/MAC 为空 |
| `networkobjects export` CSV | 2 行，地点名均为「测试校区」，退出码 0 |
| 本机 `veyon-device-directory.json` | 所有电脑都是 `Conflict`：`父地点 UUID 缺失或无法读取`，`locationName` 全为空 |

**四个问题的根因**

1. **删错档案**：I 节 22:15 的清理保留了没有 Veyon 绑定的「测试」档案，删掉了真正绑定的「测试校区」档案（见 I 节更正）。这就是「没有对应上」——名字相同不代表 UUID 对应。
2. **同步失败（决定性代码缺陷）**：`VeyonNetworkObjectDirectory.ParseNetworkObjectsDump` 的跳过条件 `(parentObjectId == Guid.Empty && Host 空 && MAC 空) → continue` 把 **Veyon 4.11.2 顶层 Location 行当成 Root 伪行丢弃**。于是 `ReadNetworkObjects()` 只返回电脑，地点 UUID 字典为空：设备目录里每台电脑都被标成「父地点 UUID 缺失」→ `FingerprintLocations(locations)` 与 `FingerprintLocations(objectLocations)` 不匹配 → `IsVeyonDeviceDirectoryCurrent=false` → `RejectUnverifiedWindowsDirectoryRoster()` 拦截 → `RefreshDeviceListsAfterSeatMapSaveAsync()` 返回 false → 截图里那句红字。这个缺陷来自仓库既有实现（HEAD 同样存在），旧的单测只覆盖了「Location 挂在 Root UUID 下」的格式，没有覆盖 4.11.2 的全零 Parent。
3. **右键删除无效**：菜单项 `IsEnabled="{Binding HasVeyonTarget}"`，而错误档案下所有座位 `VeyonObjectId` 都是 null；`RequestDeleteSeatComputer` 又要求 `VeyonObjectId: not null`，`TeacherWindow.DeleteSeatComputer` 再静默 `return` 并丢弃方法返回值。三处叠加导致右键完全没反应。
4. **新增人员无法同步**：`AddLocation` 同样依赖 `ReadNetworkObjects`（dump）查现有地点 UUID；地点被解析器丢掉后 `existingLocationIds.Length == 0`，会再 `add location` 一次，正是「重复地点又出现」的另一条路径。

**本轮修复**

- 数据：备份到 `artifacts/veyon-evidence-20261104-2240/backup-before-restore-*.json` 后，把 `campus-directory.json` 恢复为 `ebc48f0a…`「测试校区」单档案、`classroom-seat-grid.json` 恢复其 10 座位（含 6 条 Veyon UUID 绑定）、`recent-room-selection.json` 只指向该档案。
- `ParseNetworkObjectsDump`：只跳过真正的 Root 行或「未知类型且无 Host/MAC」的行；已知 `Location` / `Computer` 行即使 Parent 为全零也保留。真实 dump 实测：修复前 2 个对象（全是电脑、Location 为空），修复后 **3 个对象（1 个 Location + 2 台电脑，Location 均为「测试校区」）**。
- `tests/VeyonCampus.Checks/Program.cs`：新增 4.11.2 顶层 Location 表格夹具，断言地点对象、电脑 `Location` 映射与 `ParseLocationsFromObjects` 结果。
- 右键删除：菜单项改为动态标题（`删除学生电脑（含 Veyon）` / `删除此座位（仅本机）`）且对任何座位可用；无 UUID 时明确提示并只删本机座位；有 UUID 时按 UUID 删 Veyon 对象；两种结果都弹窗显示。`DeleteSeatComputerFromVeyonAsync` 改为**立即持久化**座位图（预览模式没有保存按钮，且 Veyon 删除不可撤销），`RemoveComputer` 加 `VeyonDirectoryMutationGate`。
- 地点对应可见：`TeacherViewModel.VeyonRoomBindingStatus` 读取座位绑定的 `VeyonLocationObjectId`，在档案下拉下直接显示「已对应 Veyon 地点「X」（UUID …）；本机已绑定 N 台电脑」或「尚未绑定…」，避免只靠显示名判断。

**预期现场行为**：装 1.1.69 并重启后，档案下拉只有「测试校区」，座位图恢复 6 个有姓名的电脑座位；页面显示 UUID 对应；右键任一学生座位可删除；点「重试 Veyon 同步」时，2 台现有电脑按 UUID 核对通过，另 4 台不存在的旧 UUID 被清理后按固定 Host（PC-07 / PC-08 / PC-12 / PC-13）重新写入同一个地点 UUID，不再新建重复地点。

> **版本说明**：1.1.68 是本轮修复的第一次打包（2026-11-04 22:59）。之后 `9b07fd2 important bug fixed`（23:09）提交了同一批桌面端修复，`dee7405 bug: phone fixed`（2026-11-05 08:37）又提交了手机端课堂开关 / 学生聊天 / 网站策略入口与手机端布局修复。**1.1.69 基于 `dee7405` 重新构建，包含以上全部内容**；安装包 `artifacts/VeyonCampus-TeacherConsole-1.1.69-Maintenance-Test.exe`，70,980,081 B，SHA-256 `1C557DF568F701A812225A8DD59C200B3536C36139FBBC3F8A59729C51FBD91F`，0 错误；内嵌手机端资源为 `veyon-campus-mobile-v58`。

**边界**：`tests/VeyonCampus.Checks` 工程在 HEAD 就有既存编译错误（`MaintenanceRun.VerifyCapabilities` 签名不匹配、`MaintenanceFaultInjectionChecks.cs` 顶层本地函数不可见），与本轮改动无关，本轮用 TeacherConsole Release 发布构建校验；自动化测试 / Windows VM 完整回归仍未运行。

---

## K. 08:48 复核：两个删除菜单项、删 13 加 14 后不同步

**用户反馈**：右键菜单同时出现「删除此座位」和「删除此座位（仅本机）」；删掉编号 13 的座位、新增一个座位自动编号 14 后，Veyon 里 13 仍在、14 没加，报错「电脑清单有重复目标，或不属于同一个 Veyon 地点」。

**两个菜单项原本的含义**

| 菜单项 | 出现条件 | 行为 |
| ------ | -------- | ---- |
| 删除此座位 | 编辑模式 | 只改本机草稿，保存前可 Ctrl+Z；**不删 Veyon** |
| 删除此座位（仅本机） | 预览模式且座位没有 Veyon UUID | 立即保存本机删除；**不删 Veyon** |
| 删除学生电脑（含 Veyon） | 座位有 Veyon UUID | 按 UUID 删 Veyon 并删本机座位 |

截图里两项同时出现，是因为该座位没有 UUID、页面又在编辑模式：旧的编辑态项和新加的无 UUID 项同时可见，语义几乎相同。**本轮改为每个座位只出现一个删除项**：绑定座位只显示「删除学生电脑（含 Veyon）」；未绑定座位在编辑模式显示草稿「删除此座位」，预览模式显示立即保存的「删除此座位」。

**「13 没删、14 没加」的根因**

- 用户用的是「删除此座位」而不是「删除学生电脑（含 Veyon）」，所以 Veyon 的 PC-13 被有意保留；这是旧菜单没有把语义讲清。
- 新增座位自动编号 14 后，同步在 `ClassroomSeatGridStore.AssignVeyonTargets` 失败：该校验要求关联清单里的 **电脑显示名唯一**，而 Veyon 当前有两台都叫「333」（PC-07 / PC-12）。显示名不是设备身份，绑定实际按 Host + UUID，因此这个校验过严，把整个绑定写入和后续新增 14 都挡住了。
- 现已移除「显示名唯一」校验，并把原来一句笼统报错拆成：重复座位 / Host 重复 / UUID 重复 / 多个地点 UUID / 编号重复，分别给出具体原因。
- 现场残留：按 UUID `c2fe597e-b463-55f5-8a29-ea345da155b0` 删除 PC-13（删除前已导出 `artifacts/veyon-evidence-20261105-0850/veyon-config.json`，删除后读回确认 Veyon 剩 1 个地点 + 5 台电脑）。

**1.1.70 预期行为**：装 1.1.70 后点「重试 Veyon 同步」，应把编号 7/8/12 三个座位按 Host 绑定到现有 PC-07/PC-08/PC-12（允许两台都叫「333」），再把编号 14 的「111」写回同一个地点 UUID；Veyon 变成 6 台，不再出现重复地点。

**交付**：`artifacts/VeyonCampus-TeacherConsole-1.1.70-Maintenance-Test.exe`，70,980,837 B，SHA-256 `CE3AE03E6F24523487D10BE1C4B77E4E4ACD26DB9BB3CE63C57B69901586909A`，0 错误。

---

## L. 09:00 复核：最后一个错误 —— 写回成功但「名单刷新失败」

**用户反馈**（1.1.70）：Veyon 已确认写入，页面仍显示

> 本机座位资料已保存；Veyon 同步未完成。可查看原因并重试。
> Veyon 写入已读回确认，但策略与课堂设备名单刷新失败；本机座位资料已保存。请检查目录读取权限后重试。
> 当前 Veyon 名单未能通过完整读取与身份核对；请查看页面诊断并在目录恢复后重试。

**先排除的嫌疑**（用实机数据跑真实代码，`.tmp-build/live-fp`）

| 检查 | 结果 |
| --- | --- |
| `networkobjects export` → 地点指纹 | `de61db00…` |
| `networkobjects dump` → 对象地点指纹 | `de61db00…`，**完全一致** |
| `ParseNetworkObjectsDump` 对象数 | 7（1 个地点 + 6 台电脑），无重复 UUID |
| 座位绑定 → `VeyonDeviceDirectoryStore.Reconcile` | 成功，6 台设备，0 冲突 |

也就是说 Veyon 侧、指纹核对、设备投影全部正常，报错与「读取权限」无关——那句提示是误导。

**真正根因**：刷新路径 `RefreshDirectoryViewsAfterVeyonWriteAsync` → `ApplyClassroomVeyonLocation` → `TeacherCampusProfileFromVeyon.MergeLocation`，后者要求**每台电脑的 Host 必须能解析成 `前缀+编号`（PC-nn）**。该地点里有一台学生机用的是 Windows 出厂名 `desktop-fgvqhia`（座位「小子」），解析失败直接抛 `InvalidDataException`，整个刷新被判为失败。只要机房里有一台不按 PC-nn 命名的学生机，**每次**写回后的刷新都会假失败。

**为什么定位绕了一圈**：错误文案写的是「请检查目录读取权限」，把真实原因（地点合并校验）盖掉了。

**修复（1.1.72）**

1. `ApplyClassroomVeyonLocation`：**已存在的机房以本机座位图为准**，不再按 Host 反推编号。机房已存在时只核对台数并给状态，不重写 `campus-directory.json`（读路径不写盘）。只有首次从 Veyon 建档才走严格合并。
2. `TeacherCampusProfileFromVeyon.MergeLocation`：允许任意**固定 DNS 名**的学生机（如 `desktop-fgvqhia`），这类目标保持"无编号"，不再让整份地点失败；IP 与非 DNS 目标仍然拒绝（设备身份必须可稳定解析）。编号列表改为排序后取最小值为起始号。
3. 电脑编号彻底改为**可选项**：`CanStartRoomCreation` 不再要求「待编号座位为 0」；写入 Veyon 前若有"已填姓名但无编号"的座位，自动按现有最大号续编（原「给待编号座位分配新号」的同一套逻辑），并在结果里说明"编号只是本机标签，Veyon 以 Host + UUID 识别设备"。
4. 错误文案不再甩锅权限：写回成功但刷新失败时，直接带出 `ClassroomDirectoryError` / `WebsiteDirectoryError` / 设备目录摘要中的真实原因；并且不再对同一次失败做第二次刷新 + 重复打印同一段话。

**1.1.72 预期行为**

- 装 1.1.72 后点「重试 Veyon 同步」或再次保存：应直接提示成功，不再出现「名单刷新失败」。
- 「地点与学生名单」页状态变为「已核对 Veyon 地点「测试校区」：6 台电脑与座位图一致；没有修改本机档案或 Veyon。」
- `desktop-fgvqhia` 这类出厂名学生机可以正常保留、绑定、上课，不需要改成 PC-nn。

**交付**：`artifacts/VeyonCampus-TeacherConsole-1.1.72-Maintenance-Test.exe`。

---

## H. 与既有文档的关系

- [设备目录同步手动验收清单](2026-11-04-device-directory-sync-manual-checklist.md)：A2、A3、D、G 节已覆盖部分场景；需在下一轮测试包中补充本节 L04/L06/L07 的回归用例。
- [设备目录同步计划](../VeyonCampus_Device_Directory_Sync_Plan.md) 与 [实现记录](2026-11-03-device-directory-sync-implementation-log.md)：设计/实现依据，本记录不修改其结论。
- [Maintenance 最终实施计划](../VeyonCampus_Maintenance_最终实施计划_Goals与问题001-009.md) 第 12 节 D-02 / D-03 与附录 C 的 B-07 同步更新。
