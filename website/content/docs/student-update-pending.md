---
id: "student-update-pending"
slug: "student-update-pending"
title: "学生端更新一直显示待确认或待核对怎么办？"
summary: "回执缺失不等于更新成功或失败；先读回版本和恢复状态，不要反复覆盖安装。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["更新待确认", "待核对", "未收到回执", "一直转圈", "更新失败", "事务恢复"]
aliases: ["学生电脑更新一直转圈", "学生端更新没有回执", "更新结果一直待确认", "学生端更新状态待核对"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "src/VeyonCampus.App/Presentation/Views/TeacherUpdatesView.axaml"]
related: ["student-update", "maintenance-service", "student-offline", "campus-identity"]
status: "published"
---

“待确认”或“待核对”表示教师端还没有取得足够的新鲜结果，不能直接理解为更新成功，也不能直接认定更新失败。

## 待确认时怎么查

1. 查看该设备最近的回执时间、更新阶段和错误说明。
2. 确认学生电脑可连接后重新读取其安装版本和后台维护组件状态。
3. 如果有恢复事务、文件缺失、权限拒绝或身份变化提示，先保留日志并联系管理员。
4. 只有确认设备状态、当前版本和恢复记录后，才决定是否重试或运行修复。

## 不要重复操作

不要连续重复更新、手工移动安装目录或删除事务材料。失败恢复可能需要保留上一版本文件，贸然清理会让管理员更难恢复。
