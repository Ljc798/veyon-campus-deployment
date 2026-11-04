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
- 本轮没有编译或运行自动化检查；静态代码审阅无法证明 Windows veyon-cli、提升流程和真实 UI 行为通过。

## 当前估算（2026-11-04）

- 按 2026-11-04 修订后的范围（固定 Windows Hostname 即 Veyon Host，不实现 IP 扫描/固定 IP/地址映射），完整“座位图与 Veyon 目录协同”代码范围约 **65%**。已有地点新增写入、读回绑定、策略名单刷新这条主链路约 **75%**；均为代码审阅估算，不代表验收。
- Windows 10/11、自动化测试、构建及现场验收均未执行；用户手动验收进度为 **0%**。

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
