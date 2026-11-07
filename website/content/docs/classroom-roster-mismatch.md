---
id: "classroom-roster-mismatch"
slug: "classroom-roster-mismatch"
title: "课堂设备列表多出电脑，怎么核对？"
summary: "按地点核对课堂目标、Veyon 电脑和座位绑定；不要只凭名称删除设备或推送策略。"
category: "troubleshooting"
audience: ["teacher", "school-it"]
keywords: ["课堂设备列表", "多出电脑", "设备数量不一致", "虚空设备", "课堂目标", "Veyon 名单", "座位图"]
aliases: ["课堂控制里多出十台设备", "课堂设备名单多出来", "为什么课堂里有虚空设备", "学生电脑数量对不上"]
verifiedFor: "1.1.77"
verificationStatus: "source-reviewed-needs-field-test"
lastReviewed: "2026-11-07"
sourceRefs: ["docs/refactor/data-source-inventory.md", "docs/refactor/root-cause-analysis.md", "docs/maintenance/classroom-feature-acceptance.md"]
related: ["duplicate-location", "seat-chart", "classroom-location", "report-issue"]
status: "published"
---

课堂页多出几台电脑时，先按地点和设备信息核对；只看总数或显示名称无法确认它们是不是重复对象。

## 逐项核对

1. 记下课堂页当前地点、显示总数，以及多出来电脑的显示名称或编号。名单未核对清楚前，不要全选设备或推送策略。
2. 如果课堂已经开始，记录开始时间和页面显示的目标数量；课堂目标可能保留开始时选中的设备。
3. 在 Veyon 中打开同一地点，查看实际电脑名称和 Host（电脑网络名称）。名称相同或无法辨认时，请管理员按 UUID（设备的唯一识别码）确认。
4. 在教师端“地点与学生名单”的座位图中核对电脑编号、Host 和已绑定设备。空座位、没有座位的电脑、尚未绑定设备的座位，会让不同页面的数量看起来不一样。
5. 如果课堂目标和当前 Veyon 设备仍对不上，先不要应用课堂策略。记录软件版本、地点、各页面显示数量和差异设备，再联系管理员排查。

## 不要为了对齐数字而删设备

不要只按显示名称删除电脑或地点，也不要为了让数字一致而解除座位绑定。若出现两个同名地点，可继续查看[处理 Veyon 中的重复地点](./duplicate-location.md)。
