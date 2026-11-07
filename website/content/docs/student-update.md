---
id: "student-update"
slug: "student-update"
title: "怎样更新学生电脑上的软件？"
summary: "先确认教师端更新来源和目标设备，再查看逐台结果；需要时安排分批更新。"
category: "maintenance"
audience: ["teacher", "school-it"]
keywords: ["学生端更新", "批量更新", "静默更新", "StudentSetup", "软件升级", "Maintenance"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["src/VeyonCampus.App/Presentation/Views/TeacherUpdatesView.axaml", "docs/学生端首次部署与恢复指南.md"]
related: ["student-update-pending", "maintenance-service", "teacher-update", "student-offline"]
status: "published"
---

更新学生电脑前，先核对教师端显示的更新来源、目标名单和学生电脑状态。不要仅凭批次汇总数字判断所有文件都已切换成功。

## 操作步骤

1. 打开“设备与更新”，查看选中的学生电脑和可用操作。
2. 如果页面显示更新源或版本信息，核对发布版本及渠道。
3. 选择需要更新的设备，阅读确认窗口与风险提示。
4. 发起更新后等待逐台回执，记录成功、失败、超时和需核对数量。
5. 对显示需核对的电脑重新读取安装版本和后台组件状态，再决定后续操作。

## 批量更新的限制

批量更新会受设备在线状态、网络、文件占用和系统权限影响。首次升级先在可恢复的测试设备上验证，必要时分批进行。具体是否支持当前来源和版本，以教师端显示的能力与现场验收为准。
