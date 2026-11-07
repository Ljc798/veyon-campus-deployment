---
id: "student-uninstall"
slug: "student-uninstall"
title: "卸载学生端失败：文件缺失或清理未完成怎么办？"
summary: "卸载会自动清理学生端更新恢复记录；遇到文件缺失、后台服务或策略清理失败时，再按具体提示处理。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["卸载学生端", "学生端卸载失败", "全部卸载", "不让卸载", "unins000.msg", "文件缺失", "Maintenance 服务卸载失败", "后台 Agent", "限制未清理", "保留安装文件", "管理员维护记录", "更新恢复记录"]
aliases: ["学生端不让卸载", "全部卸载学生端", "卸载时 Maintenance 无法移除", "后台 Agent 或策略没有清理完", "卸载程序提示文件缺失", "卸载前要不要先恢复学生端更新", "卸载学生端更新恢复记录"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["src/VeyonCampus.Core/StudentSetupUninstaller.cs", "src/VeyonCampus.Core/WebsitePolicyRuntime.cs", "src/VeyonCampus.App/Program.cs", "installer/VeyonCampus-Student.iss"]
related: ["student-update-pending", "report-issue", "student-setup"]
status: "published"
---

先不要手动删除学生端文件夹。开始卸载后，卸载程序会自行清理已遗留的学生端更新恢复记录，不需要先手动运行恢复操作。如果系统提示另一个安装或更新正在进行，等它结束后再重新卸载。其他提示按以下步骤处理：

## 按提示分别处理

1. 如果窗口提示 `unins000.msg` 缺失，说明卸载程序需要的文件不完整。请管理员确认本机版本，并使用匹配的官方学生端安装包覆盖修复；安装程序完成后，再从 Windows 应用列表重新运行卸载。不要单独从网上下载这个文件。
2. 如果提示 Maintenance 后台服务无法安全卸载，保留安装文件，请管理员检查服务状态和“管理员维护”记录，处理后再重试。
3. 如果提示学生程序已停止，但后台管理程序或学生限制没有清理完成，查看学生端部署工具里的“管理员维护”记录。记录会说明清理停在哪一步；请管理员处理后再重试。
4. 以上情况都不要手动删除 Veyon Campus 文件夹、计划任务、注册表项或浏览器策略；这些操作可能让限制留在电脑上，或影响其他软件。
5. 如果官方安装包修复后仍无法卸载，或管理员处理后再次提示清理未完成，把完整截图和版本信息交给支持人员；不要上传校区配置包、密码、学生个人资料或未脱敏日志。

## 完全卸载会清理什么

如果选择的是完全卸载，电脑上的 Veyon 配置和认证密钥、地点以及 Veyon Campus 的用户设置也会被清理。确认这些资料不再需要后再继续。
