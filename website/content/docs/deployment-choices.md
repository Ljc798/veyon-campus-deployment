---
id: "deployment-choices"
slug: "deployment-choices"
title: "学生端部署时如何选择要执行的操作？"
summary: "只勾选本次明确需要的操作，并逐项核对结果预览和影响范围。"
category: "student"
audience: ["teacher", "school-it"]
keywords: ["部署选项", "电脑名", "Veyon", "学生账户", "管理员密码", "操作预览"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "src/VeyonCampus.App/StudentSetupWindow.axaml"]
related: ["student-setup", "prepare-before-install", "deployment-check"]
status: "published"
---

部署页面显示的操作项目会因校区配置、当前电脑状态和版本而不同。不要因为一项操作已选择，就假设其他项目也安全或必须执行。

执行前先逐项看清影响范围：是否要配置 Veyon、是否要修改电脑名、是否要创建学生账户，以及是否涉及管理员密码。只保留本次经过学校批准且确实需要的项目，然后检查页面的总体预览。

## 特别注意

- 修改 Windows 电脑名通常需要重启；确认目标名称和设备编号无误。
- 新建账户或调整权限会影响本机登录；先按学校账户规则核对。
- 管理员密码变更不能由程序自动读回或恢复旧密码，必须事先安排密码交接和保管。
- 已存在但状态不明确的 Veyon 或系统组件，应先请管理员核对，不要强行覆盖。

确认后再执行，并按[部署结果怎么检查](./deployment-check.md)逐项读回。现场系统管理策略可能让某些选项不可用。
