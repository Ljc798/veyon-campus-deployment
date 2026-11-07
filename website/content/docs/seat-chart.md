---
id: "seat-chart"
slug: "seat-chart"
title: "如何编辑机房座位图？"
summary: "座位图用于整理行列、座位、学生姓名和设备关联；拖动座位不会更换设备身份。"
category: "devices"
audience: ["teacher"]
keywords: ["座位图", "拖动", "编辑座位", "换座", "行列", "UUID"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/机房座位图使用指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherRoomView.axaml"]
related: ["classroom-roster-mismatch", "print-seat-chart", "seat-names", "computer-number", "add-student-computer"]
status: "published"
---

打开“地点与学生名单”，选中地点后进入座位图。编辑座位布局只整理座位数据；电脑身份仍由设备 UUID 关联。

## 编辑时注意

1. 使用编辑模式调整行列和空座位，再把座位拖到教室实际位置。
2. 点击座位修改学生姓名或电脑编号时，先确认没有选错座位。
3. 移动座位只改变座位位置或人员安排，不要把它理解成更换了 Windows 主机或 Veyon 地址。
4. 保存后查看本机保存及 Veyon 同步结果。页面报告失败时，先处理提示，不要继续推送到不确定的设备名单。

“删除座位”和“解除电脑关联”含义不同：前者删除该座位记录，后者保留座位资料但取消设备绑定。操作前阅读确认提示，并确保有可恢复的资料副本。

首次使用或大批变更后，逐台核对 Veyon 目录、课堂名单和座位图，确认设备没有重复或错绑。相关内容见[电脑编号与学生姓名的区别](./computer-number.md)。
