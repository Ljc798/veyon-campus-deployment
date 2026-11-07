---
id: "student-config"
slug: "student-config"
title: "学生电脑的校区配置从哪里获取？"
summary: "使用本校授权教师提供的配置；配置校验失败时暂停并核对来源和校区。"
category: "student"
audience: ["teacher", "school-it"]
keywords: ["校区配置", "配置包", "云端获取", "本地导入", "配置校验", "公钥"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/学生端首次部署与恢复指南.md", "src/VeyonCampus.App/StudentCloudPackageWindow.axaml", "src/VeyonCampus.App/StudentSetupWindow.axaml"]
related: ["student-setup", "deployment-choices", "campus-identity", "download-tools"]
status: "published"
---

学生端应使用由本校授权教师提供的校区配置。配置中包含识别校区和验证材料，不能用他校配置，也不要自行改动文件。

根据当前安装版本，界面可能提供在线获取或从本地文件导入。按界面要求搜索校区、选择版本或选择配置文件后，先查看校区名称和配置校验结果，再继续。

## 校验失败时

- 确认配置是否来自本校授权教师，校区是否选对。
- 记下界面显示的完整错误文字和版本。
- 让教师核对配置包来源与校区身份。
- 在问题解决前停止部署。

不要删掉清单、编辑公钥或绕过签名验证。更换教师电脑或校区密钥属于管理员迁移事项，不能通过反复生成新配置包安全解决。相关说明见[校区身份提示如何处理](./campus-identity.md)。
