---
id: "student-setup"
slug: "student-setup"
title: "如何在学生电脑上运行部署工具？"
summary: "在学校授权、可恢复的学生电脑上运行学生端工具，逐项核对准备执行的操作。"
category: "student"
audience: ["teacher", "school-it"]
keywords: ["学生端安装", "Student Setup", "管理员", "部署工具", "Windows"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "src/VeyonCampus.App/StudentSetupWindow.axaml"]
related: ["student-config", "deployment-choices", "deployment-check", "prepare-before-install"]
status: "published"
---

在学生电脑上运行学生端部署工具前，先确认电脑属于本校区、操作者有学校授权，并且电脑有可行的恢复方式。

## 基本步骤

1. 从[官网下载安装包](/download)，确认文件名标注的是学生端。
2. 在测试电脑上启动程序；按提示确认管理员权限和配置来源。
3. 载入本校授权教师提供的校区配置。也可查看[配置从哪里获取](./student-config.md)。
4. 检查部署内容和结果预览，只选择本次确实需要执行的项目。
5. 确认后等待程序完成，并逐项查看结果；如果提示重启，先保存正在处理的工作。

部署工具可能涉及电脑名、Veyon 或账户设置。具体可选内容取决于配置和当前安装状态；请以本机页面的核对清单为准，不要接受不认识的变更。

如果程序报告校区身份、签名或文件校验错误，停止操作并请配置提供者核对。不要删除清单、替换公钥或连续重复部署。
