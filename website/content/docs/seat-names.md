---
id: "seat-names"
slug: "seat-names"
title: "如何修改座位上的学生姓名？"
summary: "座位编辑中的保存只更新 Campus 名单；此流程不会自动双向同步 Veyon Name。"
category: "devices"
audience: ["teacher"]
keywords: ["改学生名字", "姓名", "座位姓名", "换座", "Veyon Name", "电脑名称", "名称同步", "不同步"]
aliases: ["学生姓名会自动同步到 Veyon Name 吗", "Campus 改名自动同步 Veyon Name", "Campus 姓名没同步到 Veyon", "Veyon 里的电脑 Name 改过了为什么 Campus 学生姓名没跟着变", "Veyon 名称和学生姓名不一致", "Veyon Name 双向同步"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/TeacherWindow.axaml.cs", "src/VeyonCampus.App/TeacherViewModel.Classroom.cs"]
related: ["computer-number", "seat-chart", "print-seat-chart"]
status: "published"
---

在“地点与学生名单”里选中正确地点和座位，编辑学生姓名，然后保存。姓名是座位上的人员资料，不是 Windows 计算机名。

修改前先核对电脑编号、Host 和座位。保存后看见姓名更新，再确认设备 UUID、电脑编号与绑定关系没有变化。若正在处理换座，应按界面提供的换座流程操作，避免先后修改同一组座位造成覆盖。

## Campus 姓名会自动同步到 Veyon Name 吗？

当前座位编辑和读取目录流程不会自动双向同步。座位编辑框里的“保存”只更新教师端的 Campus 座位名单，不会自动修改 Veyon 电脑对象的 Name；读取 Veyon 电脑名单也不会自动用 Veyon Name 覆盖 Campus 学生姓名。

如果两处名称不一致，先按设备 UUID、所在地点和 Host 确认它们确实对应同一台电脑，再分别核对两处姓名。不要仅凭学生姓名或电脑编号匹配设备，也不要因此修改 Windows 计算机名、Host、UUID 或座位绑定。

姓名留空时可以不填写；界面会显示未填写状态。不要用学生姓名作为电脑编号，也不要为了改显示姓名而重命名 Windows 主机。
