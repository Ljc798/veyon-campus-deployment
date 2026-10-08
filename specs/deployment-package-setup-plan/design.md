# 校区配置包部署建议设计

## 组件边界

TeacherConsole 维护四项建议开关并把值交给 `PackageBuilder`。`PackageBuilder` 生成 schema v6 manifest，并通过现有暂存、严格文件集合校验、`PackageContext.Load` 和摘要读回流程确认生成结果。StudentSetup 由 `PackageManifest` 解析建议并绑定到不可变 `PackageContext`；ViewModel 将建议初始化为可编辑操作选择，后续由现有预检、计划冻结、风险说明和“确认计划并执行”控制实际修改。

CloudBase 只校验和保存包：Node 校验器与数据库迁移接受 schema v6，配置对象路径使用 `deployment-packages/v6/`。不改策略 API 或应用发行清单。v6 迁移与 API/OPA 已在预备份后部署到共享体验环境；线上 v5/v6 写入、读取、撤回和清理 E2E 另行验收，不由本实现自动运行。

## schema v6

- v1–v5 分支保持原样，避免把新语义追溯套用到旧包。
- v6 沿用 v5 的网站、应用、系统策略公钥、兼容范围和六文件 SHA-256 清单；新增严格对象 `recommendedOperations`，由四个必填布尔字段组成。
- 字段直接写入 `manifest.json`，不新增额外载荷文件；`PackageContext.PackageFingerprint` 已包含 manifest 摘要，因此建议值会随包内容变化而被检测。
- Teacher 生成后必须重新解析 v6 包，并逐项比较建议值和本次用户输入；若不一致，拒绝发布。
- Node API 以同一规则验证 v6，并将规范包存入 `deployment-packages/v6/`。数据库 schema version CHECK 与发布 RPC 参数校验共同允许 3、4、5、6；历史版本继续保留。

## 建议与授权的分离

Teacher 界面将区域标注为“学生端建议操作”，默认仅勾选安装/配置 Veyon。StudentSetup 成功读入 v6 包时，在尚未有人编辑操作选择的初始状态下应用前三项建议；第四项始终保持未勾选，并在操作卡片和最终执行计划中显示“包内建议，须先确认本机管理员账户后手动选择”。一旦维护人员改过任一操作，包重读或预检不得覆盖其选择。学生维护人员仍需查看完整计划并显式确认；取消和页面导航不能触发部署。

该规则将“未知设备”定义为没有经过独立设备身份登记的学生机。当前通用校区包没有设备级绑定，因此所有学生机均按未知设备处理。即使教师误把 `changeAdminPassword` 设为 true，导入也只显示提醒，不自动选择、读取或保存任何密码。

## UI 呈现

- Teacher 复用现有 Avalonia 表单、选项卡片和说明文字，不重做导航或视觉系统；文案明确这是首次部署建议。
- Student 在操作区显示建议来源、被建议的操作以及一键取消单项的现有复选框；密码建议使用警示提示和未选中状态。
- 预检与执行摘要分别列明“已选执行项”和“包内建议但未选择项”，便于维护人员识别哪些内容来自配置包，哪些是自己确认的。

## 错误与兼容

manifest 出现未知字段、错误 schema/compatibility 组合、非布尔建议、重复 JSON 字段或摘要不符时，Student 与 API 均失败关闭。v1–v5 不含建议字段，不产生建议选择。较旧 StudentSetup 不支持 v6，因此 Teacher 生成包的兼容范围和 UI 必须说明需使用支持 v6 的 StudentSetup；云端不把 v6 包伪装为旧 schema。

## 验证

1. Core 检查覆盖 v6 往返、严格字段、篡改、重复/非布尔字段、v1–v5 回归和建议指纹绑定。
2. ViewModel 检查覆盖前三项默认建议、管理员密码永不自动勾选、用户取消后状态稳定、重读包不覆盖用户操作、预览及取消不执行。
3. Node/API 检查覆盖 v6 合法包、非法建议字段、schema 路径与 3–5 版本兼容。
4. Student/Teacher Release 构建和 docs/spec 链接检查通过后，方可将代码任务标为完成。共享 CloudBase v6 写入 E2E 需本机 CloudBase 管理员交互认证；Windows 实际账户/操作效果仍按设备验收任务记录。
