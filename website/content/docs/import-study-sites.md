---
id: "import-study-sites"
slug: "import-study-sites"
title: "如何导入常用学习网站白名单？"
summary: "导入只会把教师电脑的常用名单填入当前编辑内容；检查后仍需单独应用。"
category: "classroom"
audience: ["teacher"]
keywords: ["导入学习网站", "常用白名单", "学习网站", "保存预设", "网站名单"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["src/VeyonCampus.App/Presentation/Views/TeacherPolicyView.axaml", "src/VeyonCampus.App/TeacherViewModel.cs", "src/VeyonCampus.Core/WebsitePolicyPresetStore.cs"]
related: ["website-policy-modes", "apply-website-policy", "end-class"]
status: "published"
---

“导入学习网站”会把教师电脑上保存的常用网站名单填入当前编辑名单。它不会自动向学生电脑推送，也不会立即改变课堂中的网页访问。

## 操作步骤

1. 在“策略与预设”查看当前模式和学生电脑目标。
2. 点击“导入学习网站”。
3. 检查导入的域名，删除不适用于本校的项目，补充学校批准的网站。
4. 选择适合的限制模式，确认名单外网站的处理方式。
5. 选中目标学生电脑并按页面提示应用策略；逐台查看回执。

## 名单保存在哪里

常用名单保存在教师电脑上。若你编辑名单后没有保存预设，新内容可能只影响当前编辑草稿；如果保存预设，后续导入会使用保存后的内容。现场操作时以当前页面的保存状态提示为准。
