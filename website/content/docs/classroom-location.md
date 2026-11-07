---
id: "classroom-location"
slug: "classroom-location"
title: "如何准备机房地点和电脑名单？"
summary: "先选择正确的 Veyon 地点，再核对电脑身份和学生座位关联。"
category: "teacher"
audience: ["teacher"]
keywords: ["机房", "教室", "地点", "Veyon Location", "电脑名单", "UUID", "重试读取 Veyon 学生名单"]
aliases: ["怎样把 Veyon 现有的机房电脑名单导进来", "导入 Veyon 电脑名单", "读取 Veyon 学生名单"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml"]
related: ["seat-chart", "add-student-computer", "duplicate-location", "seat-names"]
status: "published"
---

在“地点与学生名单”中选择学校正在使用的 Veyon 地点，然后核对电脑、座位和学生姓名是否对应。不要只根据地点名称判断设备身份。

若教师端未读到当前 Veyon 电脑名单，可点“重试读取 Veyon 学生名单”。该操作只读当前 Veyon 地点与电脑列表，不会修改 Veyon 或本机座位图；读取后仍需逐项核对设备身份和座位关联。

设备会通过内部 UUID 关联。电脑编号和学生姓名是教师维护的显示资料；修改姓名或移动座位不应改变设备 UUID 或 Host。新增电脑、重新绑定设备或处理重复地点时，应先确认选中的确实是目标机房。

## 核对步骤

1. 选择正确地点，确认显示的电脑数量和名单符合预期。
2. 对照座位图逐台检查编号、学生姓名和电脑关联。
3. 新增、移除或重新绑定前，确认受影响的座位和设备身份。
4. 保存后查看页面报告的 Veyon 同步结果；读回失败时不要继续对名单不符的设备推送策略。

第一次导入或同步可能受教师账户权限、Veyon 目录状态和当前代码版本影响。现场操作应核对页面提示，必要时请管理员先备份相关资料。
