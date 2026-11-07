---
id: "computer-number"
slug: "computer-number"
title: "电脑编号和学生姓名有什么区别？"
summary: "电脑编号标识座位上的设备位置，学生姓名表示当前人员安排；二者都不替代设备 UUID。"
category: "devices"
audience: ["teacher"]
keywords: ["电脑编号", "PC-01", "学生姓名", "改名", "Host", "UUID", "座位"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/TeacherWindow.axaml.cs"]
related: ["seat-chart", "seat-names", "add-student-computer"]
status: "published"
---

电脑编号（例如 `PC-03`）用于教师识别座位或设备；学生姓名记录当前坐在该位置的学生。设备本身还通过内部 UUID 关联。

学生换座时，应更新相应的学生姓名或座位绑定；不要因此改 Windows 计算机名、Veyon Host 或 UUID。电脑更换时，则要先确认新设备身份，再由教师在设备目录中重新绑定。

## 变更前后怎么检查

1. 确认当前地点和要修改的座位。
2. 只修改需要变化的学生姓名或座位位置。
3. 保存后核对设备 UUID、Host 和编号仍对应原设备。
4. 若确实更换了电脑，使用设备重新绑定流程并核验 Veyon 目录。

姓名为空时，界面可能显示“未填写”。这表示没有维护学生姓名，不代表电脑离线或设备丢失。
