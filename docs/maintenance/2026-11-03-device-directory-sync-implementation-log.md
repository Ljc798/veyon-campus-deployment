# 统一设备目录双向同步 · 实施记录

> 对应计划：[VeyonCampus_Device_Directory_Sync_Plan.md](../VeyonCampus_Device_Directory_Sync_Plan.md)（UD-01～UD-12）
> 执行基线：本地 HEAD e691b62。开始时工作区已有未提交代码与文档；本轮在原状态上继续修改，没有重置或覆盖。
> 执行边界：用户明确要求暂不测试、教师机尚未开机。本轮只做代码审阅和编辑，不运行测试、构建、安装、打包，不连接 Windows/生产环境。
> 手工验收清单：[2026-11-04-device-directory-sync-manual-checklist.md](2026-11-04-device-directory-sync-manual-checklist.md)

## UD-01：已有地点新增电脑调用链已审计

已确认根因：

```text
教师点击座位格
  → TeacherWindow.SeatCellClicked
  → TeacherViewModel.SaveSeatCell
  → ClassroomSeatGridStore.SaveSeat
  → 只保存教师端本地座位图，没有调用 Veyon 写入
```

单独的“创建地点和学生电脑”按钮才会走：

```text
TeacherRoomView → TeacherWindow.AddRoomToVeyon
  → TeacherViewModel.AddRoomToVeyonAsync
  → ElevatedWorkerClient(AddVeyonRoom)
  → PrivilegedWorkerDispatcher
  → VeyonNetworkObjectDirectory.AddLocation
  → Veyon 读回 → 本机档案保存 → UUID 座位绑定
```

策略名单由 ReadWebsiteLocationsAsync 单独读取。旧流程既要求第二次手动操作，也没有在写入后刷新已加载的策略目标。

## UD-02/03/05：新增座位自动补齐与名单刷新（代码已改；待用户手测）

- Windows 教师端保存未关联且有姓名的座位后，自动调用受控 Veyon 写入流程；继续编辑时每次成功后更新撤销基线，避免“取消本地改动却保留已提交 Veyon 对象”。
- 写入清单使用固定电脑编号生成的 Windows Hostname（如 PC-19），并将其作为 Windows Hostname 与 Veyon Host；Veyon Name 使用学生姓名，空姓名时回退 PC 编号。
- 写入后仍需读回 Veyon 对象 UUID、Host 与所属地点，再绑定座位。遇到唯一 Host 的本地点已有电脑时保留原对象并关联；跨地点重复、同名异 Host、同 Host 多对象或父地点 UUID 不匹配均阻止操作。
- Veyon 写入成功后重新读取地点；若策略目标原本完整来自同一地点，则更新电脑名单、保留现有勾选状态，并将新增电脑设为未勾选。手工编辑过的目标文本不会被覆盖。
- 进入「课堂」或「更新」页面时会重新读取地点名单；当页停留期间的外部目录变化仍需手动刷新或重新进入页面才会读取。
- 若教师机已有课堂正在进行，座位保存保留在本机，并提示下课后再写 Veyon。
- 失败时不显示全部成功；需要核对的写入结果会保留本地座位，更新座位编辑撤销基线，避免撤销掉可能已提交的设备。
- 电脑清单现按“有电脑编号的座位”生成；无姓名但有编号的物理电脑可以进入预览、冲突检查、写入和 UUID 读回关联，姓名保持空值。无姓名且无编号的桌位不生成电脑。

## UD-04：目录读取诊断与安全回退（代码已改；待 Windows 实测）

- 增加机器可读 VeyonDirectoryReadException / VeyonDirectoryReadDiagnostic，包含失败阶段、子命令、退出码、受限长度的详情、最近成功读取时间和配置指纹。
- networkobjects export 失败或未生成文件时，用单次 dump / config export 读取结果恢复地点展示，避免重复读取两次而拿到不同快照。
- 回退结果标为 PartialRead；仅供只读地点展示，写入和 UUID 关联路径拒绝使用该结果。
- 教师端页面在使用只读回退时显示提示。没有持久化跨进程的可信旧快照，完整的 stale 缓存治理仍未完成。
- 静态复核发现：完整配置里若 `BuiltinDirectory` 和旧版 `LocalData` 都缺失，旧逻辑会把“无法识别/可能是外部目录”的配置当成空名单。现已改为拒绝解析并提示可能是外部目录；尚未实现对 LDAP/AD 配置的正向识别。

## UD-03：手工补录设备的显式关联（首版代码已改；待 Windows 手测）

- 座位编辑区提供座位和 Veyon 目标的人工选择；只展示当前地点、完整读取并通过基本冲突检查的对象。
- 点击关联时重新读取 Veyon 完整对象清单，按 UUID 核对 Name、Host、父地点，并检查 Host 唯一；座位已有另一 UUID 时拒绝覆盖。
- 不从名称、电脑编号或 Host 自动猜测身份；关联只更新本机座位绑定，不创建 Veyon 对象。

## UD-06：UUID 设备投影（首版代码已改；尚未成为全应用统一目录）

- 新增按 Veyon Computer UUID 保持稳定 `DeviceId` 的本机 JSON 投影；写入使用文件锁、临时文件和原子替换，并限制文件大小、设备数和链接路径。
- 只在完整可信目录读取后进行对账，记录 `Discovered / Renamed / HostChanged / Moved / MissingInVeyon / Conflict`；UUID 消失后保留旧条目，UUID 重建时不继承旧身份。
- 当前教师端将该投影用于冲突摘要及策略目标拦截；只读回退不会覆盖可信投影。
- TeacherViewModel 暴露最近一次已验证的不可变 `VeyonDeviceDirectorySnapshot`；策略/维护目标按快照 Host 唯一匹配 UUID，且仅允许 InSync/Discovered 状态。Name/Host/地点变化、冲突、缺失、异常/回退或指纹不一致时禁止操作；异常/回退时保留最近快照供只读查看。监控和移动端尚未改为共同消费该属性。
- 已将 `SeatId`、电脑编号和学生姓名按 Veyon UUID 并入投影；若 UUID 与座位绑定的 Host/地点不一致、同一 UUID 被多个座位绑定，或座位引用了当前及历史投影均未知的 UUID，本次投影更新会安全拒绝。
- 完整座位快照确认解除绑定时会清除投影中的旧 SeatId、编号和姓名；未提供座位快照的调用继续保留缓存资料。
- 该投影仍未作为策略、监控、移动端共同消费的不可变快照，Agent 信任状态也未合并；UD-06 仍属部分完成。

## UD-07：Veyon Name 与学生姓名双向同步（代码已接入；待 Windows 手测）

- 新增电脑以固定编号生成 Windows Hostname 并写入 Veyon `Host`；Veyon `Name` 使用学生姓名，空姓名回退 Host。设备 UUID 为身份键。
- 座位交换按各自 UUID 同步对应 Veyon Name，包括一个座位为空的情况；Host、Windows 主机名、电脑编号、UUID 与设备座位绑定保持不变。
- 本轮补充：Campus 座位改名按绑定 UUID 写回 Veyon Name，写入前核对 UUID/Host/地点，外部 Name 已变化时要求教师确认，完成后读回核验；读取目录也可列出 Name 差异，教师确认后以一次本地原子写入把 Veyon Name 同步回 Campus。
- 改名仍只允许使用 Veyon 的受控改名事务；目标由 UUID、父 UUID、Host 和目录指纹共同约束。
- Hostname 不一致的目录项应保持冲突并阻止策略操作；Windows 实测未执行。

## 当前未完成范围

- Veyon 原生目录变化的自动发现/持续对账、全应用统一不可变快照及缓存版本尚未完成；名称双向同步目前从座位图的“读取当前地点 Veyon 电脑”入口执行。
- Host/所属地点编辑不再是目标；按新目标 Hostname 必须固定且等于电脑名，不一致时应标记冲突。旧 Veyon rename worker/service 尚未退役。持久 operation journal、LDAP/AD 后端探测尚未完成。新增地点和改名使用本机 named mutex，但不同教师机之间没有共享写锁。
- 移动端、监控等页面尚未切换到统一快照；设备投影也未与 SeatId/学生名/Agent 身份合并。
- 2026-11-03 初始审阅未编译或运行自动化检查；静态代码审阅无法证明 Windows veyon-cli、提升流程和真实 UI 行为通过。2026-11-04 的后续构建与打包记录见下文。

## 当前估算（2026-11-04）

- 按 2026-11-04 修订后的范围（固定 Windows Hostname 即 Veyon Host，不实现 IP 扫描/固定 IP/地址映射），完整“座位图与 Veyon 目录协同”代码范围约 **65%**。已有地点新增写入、读回绑定、策略名单刷新这条主链路约 **75%**；均为代码审阅估算，不代表验收。
- Windows 10/11、自动化测试及现场验收均未执行；用户手动验收进度为 **0%**。2026-11-04 已完成 TeacherConsole Release 构建和离线打包。

## 后续顺序

- [x] UD-01：审计现有地点新增路径
- [x] UD-02 代码：新增未关联座位后自动写入并读回
- [~] UD-03/05 代码：人工确认关联及名单刷新首版已接入；跨页面一致快照和手测待做
- [x] UD-04 代码：结构化诊断和只读回退
- [~] UD-06 代码：UUID 投影与基本冲突识别已接入；统一快照/持久绑定整合待做
- [~] UD-07 代码：按 UUID 双向同步 Campus 姓名与 Veyon Name 已接入；座位交换同步姓名且保留设备绑定；待 Windows 手测
- [~] UD-08：新增 Hostname 一致性已按新范围实现首版；仍需清理历史覆盖兼容引用和手测
- [ ] UD-09～UD-12：完整 UI、操作日志、LDAP/AD 边界和用户验收
- [ ] 用户手动完成 Windows 10/11 验收

## 2026-11-04 后续：构建修复与测试包

- 修复本轮 ViewModel 拆分遗漏的三个课堂集合初始化，避免 `ClassroomEventItems`、`ClassroomChatThreads`、`ClassroomTaskProgressItems` 在构造后为 null。
- 曾临时增加策略页“刷新地点与电脑名单”入口；按最新交互要求已移除，保存座位后由系统自动刷新策略和课堂控制名单。
- TeacherConsole Release `win-x64` 直接构建成功，0 个错误、1 个非阻塞 `CS0649` 警告。警告指出当前 `_lastStudentUpdatePerformanceReport` 没有生成赋值，因此“导出本次匿名性能汇总”当前不可用；这不影响设备目录同步测试包启动或本轮构建。
- 已生成离线测试安装包：`artifacts/device-directory-sync-test-20261104/VeyonCampus-TeacherConsole-DeviceDirectoryTest-1.1.45-win-x64-refresh.exe`（70,931,384 字节，SHA-256 `427943299A6DF2237F924B9A69403239AF2917CD92E29018E14F098D1A64FA8E`）。
- 自动化测试和 Windows VM/现场测试仍未运行；本包用于用户隔离环境手测。旧安装包未覆盖。

## 2026-11-04 后续：座位布局编辑重构

- 座位编辑现在使用本机草稿：点击“编辑座位”后可增删行列、编辑/删除单座位、拖动移动或交换座位；行列和实际座位数量分离，Ctrl+Z / Ctrl+Shift+Z 可撤销和重做。
- 保存先落盘本机布局，再按固定 Host 唯一匹配读回 UUID；已有绑定按 UUID 安全同步 Name，新电脑走既有提权写入与读回验证。设备身份冲突、权限错误或刷新失败会显示部分成功和重试入口；移动/删除座位不会删除 Veyon 对象。
- 姓名冲突由教师选择采用本机姓名或 Veyon Name，所有写入仍核对 UUID、地点和 Host。保存期间锁定编辑控件。
- “打印座位表”保留。
- Release `win-x64` 1.1.48 构建成功（0 错误、1 个 `CS0649` 警告）；自动化测试、Windows VM 和现场验收未运行。
- 最新测试包：`artifacts/seat-editor-test-20261104-1.1.48/VeyonCampus-TeacherConsole-SeatEditor-1.1.48-win-x64.exe`，70,937,933 字节，SHA-256 `7DB3F94DC8D85E7409B5196856EF3DB1870BD07B853206CD64A32AAAD773256B`。1.1.47 包已由本包替代。
- Maintenance 尚未完成：计划仍标注多个 P2/P3 和 CM 任务为部分接线/未验证；匿名学生更新性能汇总导出没有 Coordinator 数据赋值，`_lastStudentUpdatePerformanceReport` 的 `CS0649` 警告仍存在。Maintenance 自动化和 Windows VM/现场验证也仍未完成。

## 2026-11-04 后续：空座位图导入、名单选择与拖动反馈

- 进入“地点与学生名单”时，如果已选本机地点的座位表为空，教师端会只读 Veyon 配置；只在地点名称唯一、UUID 唯一、Host 唯一且匹配本机固定 PC 编号时，按电脑编号将 Veyon Name 和设备关联填入本机座位图。导入使用本机原子初始化，目录冲突时不做部分导入、不覆盖现有座位，并提供诊断和重试；这一步不写 Veyon。
- 策略与预设恢复学生复选名单、全选和清空选择；其勾选范围仍与设备概览及维护页面共用。拖动座位时，鼠标所在目标格新增蓝色高亮反馈。
- “打印座位表”保留。Release `win-x64` 1.1.49 构建成功，0 个错误、103 个警告（应用中的 `CS0649` 匿名性能报告字段未赋值仍未处理）；离线安装包已生成。
- 最新测试包：`artifacts/seat-editor-test-20261104-1.1.49/VeyonCampus-TeacherConsole-SeatEditor-1.1.49-win-x64.exe`，70,941,951 字节，SHA-256 `68FE5CA17E26747117F22DE50CB5F6264A7970E4DC63A3B534C0DD269F674C86`。
- 自动化测试、Windows VM/教师机和现场验收尚未运行。Maintenance 仍未完成，待办项及未验证范围见计划文档。

## 2026-11-04 后续：按 UUID 导入 Veyon 设备与行列悬浮删除

- 空座位图自动导入不再要求 Veyon Host 与本机 `PC-编号` 完全相同；导入仍要求当前地点、UUID 和 Host 唯一有效。Host 不匹配本机编号时保留 UUID/Host/地点绑定，不猜编号、不改 Host，姓名可按 UUID 同步。
- 保存对账先核验已有 UUID 绑定，再按唯一固定 Host 和当前地点绑定尚未关联的新编号座位；Veyon 写入计划只包含未绑定的编号座位，因此导入的既有设备不会重复创建。空姓名但已有 UUID 的座位不再被自动补电脑编号。
- Veyon 多于空座位图时导入所有可唯一核验设备；Campus 新增的未绑定编号座位只在保存时写入 Veyon。保存完成仍自动读回并刷新策略与课堂设备名单。
- 行、列标题在编辑模式悬浮时显示 `−`；点击空行/空列弹出确认框，含座位的行列弹出提示并阻止删除。打印座位表保留。
- TeacherConsole Release `win-x64` 1.1.50 构建成功，0 个错误、103 个警告（包含已有的跨平台分析和 `_lastStudentUpdatePerformanceReport` 未赋值警告）；离线安装器打包成功：`artifacts/seat-editor-test-20261104-1.1.50/VeyonCampus-TeacherConsole-SeatEditor-1.1.50-win-x64.exe`（70,939,475 字节，SHA-256 `F8653B9688E1280DF3FEA7E88D69D0E02FD2098EA24B7DB366B07AE51BDFBD10`）。
- 构建日志：`artifacts/seat-editor-build-20261104-1.1.50.log`；离线打包日志：`artifacts/seat-editor-package-20261104-1.1.50.log`。
- 自动化测试、Windows VM/教师机和现场验收尚未运行；Maintenance 仍未完成。测试步骤已更新到[手动验收清单](2026-11-04-device-directory-sync-manual-checklist.md)。

## 2026-11-04 后续：同名地点恢复、Veyon 读回兼容和无编号空座位

- 用户在 Windows 手测中确认，同名本机地点档案冲突时原有座位图可被恢复；新逻辑仅在冲突座位属于同一档案、保存的 Veyon 地点 UUID 与当前地点一致、显示地点名称也一致时自动切回旧档案，不会解除绑定或改写座位。
- 用户继续测试时报告本机座位保存成功，但 Veyon 目录读回报“对象标识或类型无效”。根因仍需 1.1.54 的现场输出验证；读取器已按 Veyon 4.11.2 的对象类型区分机房/电脑与根、标签、桌面分组等对象，非目标对象不再要求 UUID；已知对象 UUID 仍要求有效且全局唯一。失败诊断增加对象序号和 dump/config export 两阶段原因。
- 修复编辑器新增空座位时会自动建议电脑编号，以及空编号被误当作重复编号的问题。只有填写学生姓名时才自动建议编号；未填写姓名且编号留空的空座位可重复添加，保存于本机且不进入 Veyon 写入计划。界面状态会说明其不会创建 Veyon 电脑。
- TeacherConsole Release `win-x64` 1.1.54 构建成功，0 个错误、103 个警告；离线安装器打包成功：`artifacts/seat-editor-test-20261104-1.1.54/VeyonCampus-TeacherConsole-SeatEditor-1.1.54-win-x64.exe`（70,945,467 字节，SHA-256 `075AEECBA79516B856A9994A579518E5F2B1D752E7C7CBD4384C9C669D966D22`）。
- 构建日志：`artifacts/seat-editor-build-20261104-1.1.54.log`；打包日志：`artifacts/seat-editor-package-20261104-1.1.54.log`。
- 1.1.54 仍待用户验证 Veyon 读回和空座位保存。自动化测试未运行；统一目录、Maintenance 后续项和全应用快照工作仍未完成。
- 针对拖动反馈滞后的修正已并入同一测试包：鼠标移动处理直接挂到座位网格，拖动启动阈值从 6 降到 3 像素；目标格使用不触发布局尺寸变化的高亮，并在网格下方实时显示目标行列。1.1.54 Release 重建仍为 0 个错误、103 个警告，重打离线安装包；构建和打包日志沿用同版本日志文件。该交互尚待用户实机验收。

## 2026-11-04 后续：UUID 重复兼容与座位右键删除

- 用户报告 1.1.54 读取失败，错误指向目录第 4 个对象的 UUID 重复；同时座位右键删除没有生效。
- 目录读取现在只对地点和电脑这些绑定目标校验 UUID：根、标签和桌面分组不会误拦截；同一地点/电脑 UUID 若重复且对象内容完全相同则合并一条，若同 UUID 对应不同内容仍拒绝读取，防止歧义绑定。
- 右键菜单通过 `ContextMenu.PlacementTarget.DataContext` 显式取得当前座位；删除项仅在编辑模式且目标是座位时启用，点击后继续走既有二次确认和本地草稿删除流程，不删除 Veyon 电脑。
- TeacherConsole Release `win-x64` 1.1.55 构建成功，0 个错误、102 个警告；离线安装器已打包：`artifacts/seat-editor-test-20261104-1.1.55/VeyonCampus-TeacherConsole-SeatEditor-1.1.55-win-x64.exe`（70,945,858 字节，SHA-256 `AE0D085E2922F6DE6AC3CB09621AB9000F063237B2DB6873016FBF42259B5B3C`）。
- 构建日志：[seat-editor-build-20261104-1.1.55.log](../../artifacts/seat-editor-build-20261104-1.1.55.log)；打包日志：[seat-editor-package-20261104-1.1.55.log](../../artifacts/seat-editor-package-20261104-1.1.55.log)。自动化测试未运行；目录读取和右键删除仍待用户 Windows 实测。

## 2026-11-04 后续：跨页面旧名单、保存后未同步与右键恢复

- 用户反馈 1.1.55 的“设备与更新”和“课堂控制”仍显示并不存在于 Veyon Configurator 的旧电脑；编辑后界面曾显示关联成功，但 Veyon 目录仍未更新；右键删除仍无效。
- 根因之一是 Windows 页面用本机地点规划电脑数填充策略/维护目标，并在课堂无活动会话时用本机 `ComputerCount` 展示课堂设备数。现在 Windows 上这几页改为以最近一次 Veyon 目录读取为准；读回失败清除过期本机目标、显示诊断并禁止未核验目录启动课堂。座位页面进入时也先读取当前目录，再进行空图导入，避免并发读取互相覆盖诊断。
- 保存失败分支也执行只读目录刷新；重试仍使用原有写入、UUID/Host/父地点核对和读回验证。人工建立座位与设备关联时明确提示“只保存在本机，保存后才写入 Veyon”，避免将本机关联误报为目录写入成功。
- UUID 完全缺失时，只有完整目录快照确认旧 UUID 不存在、座位固定编号与 `PC-编号` Host 一致，并且该 Host 在 Veyon 中不存在或唯一位于当前地点，才允许安全解绑旧的本机 UUID、按固定 Host 重建或重新关联。重复 Host、UUID 仍存在但移动/Host 改变、地点不唯一等情况继续拒绝自动覆盖；本机清理使用期望 UUID/地点/Host 守卫，保留学生姓名、电脑编号和 Veyon 对象。
- 初次导入冲突仍不覆盖或解除已有档案；若所有冲突指向一个本机档案，提供打开原座位图入口。编辑模式右键菜单显式从 `PlacementTarget` 传递座位 DataContext，支持“删除此座位”和“解除本机电脑关联”；解除关联保留学生与电脑编号、支持保存前撤销，不删除 Veyon 对象。预览模式不打开该菜单。
- Release `win-x64` 1.1.56 清理构建成功：0 错误、103 警告。离线安装器：`artifacts/seat-editor-test-20261104-1.1.56/VeyonCampus-TeacherConsole-SeatEditor-1.1.56-win-x64.exe`，70,948,757 字节，SHA-256 `576F06EC5977537CB377341166F0530500D727166494B777982BE1F43F7D312B`。警告数包含完整依赖重新编译产生的现有平台分析警告及 `_lastStudentUpdatePerformanceReport` 未赋值警告。
- 构建日志：[seat-editor-build-20261104-1.1.56.log](../../artifacts/seat-editor-build-20261104-1.1.56.log)；打包日志：[seat-editor-package-20261104-1.1.56.log](../../artifacts/seat-editor-package-20261104-1.1.56.log)。自动化测试未运行；Windows 用户验收待用户执行（`TEST-DEFERRED` / `VM-PENDING`）。打印座位表功能保留。
