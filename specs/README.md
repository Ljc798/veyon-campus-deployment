# 专项规格索引

专项规格把一个功能域的可验收需求、技术设计和细分实施任务放在一起。本页只提供导航；项目级完成状态与优先顺序统一以[开发路线与任务清单](../docs/开发路线与任务清单.md)为准，按日期的证据以[实施记录索引](../docs/records/README.md)为准。专项任务勾选表示该规格范围内的工作完成，不自动代表 Windows/手机实机验收通过。

| 功能域 | 需求 | 设计 | 子任务 | 边界 |
| --- | --- | --- | --- | --- |
| Worker 与按需 UAC | [requirements](p3-worker-uac/requirements.md) | [design](p3-worker-uac/design.md) | [tasks](p3-worker-uac/tasks.md) | 受限高权限 Worker 的协议与身份；Windows 安全/恢复验收仍开放 |
| 学生机长期系统策略 | [requirements](student-system-policy/requirements.md) | [design](student-system-policy/design.md) | [tasks](student-system-policy/tasks.md) | 六项基线、Agent/教师/更新能力；Windows 策略和恢复实测仍开放 |
| 手机教师控制基础与网站/应用策略 | [requirements](mobile-teacher-control/requirements.md) | [design](mobile-teacher-control/design.md) | [tasks](mobile-teacher-control/tasks.md) | 校园 LAN 配对、状态读取和网站/应用操作；真实手机、证书和 VLAN 验收仍开放 |
| 手机控制长期系统策略 | [requirements](mobile-system-policy-control/requirements.md) | [design](mobile-system-policy-control/design.md) | [tasks](mobile-system-policy-control/tasks.md) | 桌面保存预设、手机查看/启用/解除；不得绕过学生策略事务 |
| CloudBase 分发与双端更新 | [requirements](cloud-updates-and-installers/requirements.md) | [design](cloud-updates-and-installers/design.md) | [tasks](cloud-updates-and-installers/tasks.md) | 角色安装包、云端发行目录、Teacher/Student 更新；生产发行和设备验收仍开放 |

## 文档主责

- `requirements.md` 维护用户需求、验收条件和范围，不记录当天临时状态。
- `design.md` 维护组件边界、协议、状态机、权限和失败恢复策略。
- `tasks.md` 维护该功能域细分工作及对应证据；项目 P 编号的完成判定仍回到项目路线主表。
- 新验收发现先归入对应需求或子任务，再同步架构页和项目路线；历史记录只追加，不覆盖既有环境快照。
