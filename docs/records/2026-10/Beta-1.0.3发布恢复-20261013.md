# 1.0.3 Beta 发布恢复记录

日期：2026-10-13

源码标签：`beta-v1.0.3` → `a9b853bb39e0d392c1803f19562c15d527007eff`

安装包构建：GitHub Actions run [38282511828](https://github.com/Ljc798/veyon-campus-deployment/actions/runs/38282511828) 的 `build-installers` 作业成功；该旧 run 总体失败是因为 Gitee 上传中断。恢复流程复用了该成功构建产物，没有移动或重打产品标签。

## 发布结果

- [GitHub Beta Release](https://github.com/Ljc798/veyon-campus-deployment/releases/tag/beta-v1.0.3) 已发布为预发布，包含 Student 安装包、Teacher 安装包和 `SHA256SUMS`。
- [Gitee Beta Release](https://gitee.com/jack7981/veyon-campus-deployment/releases/tag/beta-v1.0.3) 已发布。最初版本用分卷绕过附件大小限制；本次调整后改为 Teacher、Student 各一个 GitHub 完整 EXE 直链，并清理旧分卷及合并脚本附件。
- 恢复流程 [Actions run 38295101388](https://github.com/Ljc798/veyon-campus-deployment/actions/runs/38295101388) 最终成功。它验证了原标签构建、Windows 分卷合并器、GitHub Release 资产以及 Gitee Release 的附件回读和完整文件 SHA-256。

| 文件 | 大小 | SHA-256 |
|---|---:|---|
| `VeyonCampus-Student-Setup-1.0.3-win-x64.exe` | 145,444,177 bytes | `97fd9165f1ca6a61b2d43462f7b31498ee11dc1a17b78e16c199e827dc7cdae0` |
| `VeyonCampus-Teacher-Setup-1.0.3-win-x64.exe` | 127,842,564 bytes | `6cd30d33bdc5ecb21b0a0c0f2299fd029519da1a2e3d3a36dab13a9b231af944` |

## 修复和验证

- 发布作业先创建或验证 GitHub Release，再发布 Gitee，保证 Gitee 上传失败时 GitHub 安装包仍可下载。
- Gitee 上传将大安装包切成小于平台上限的分卷，支持失败后续传；发布完成后逐个下载回读，重新计算分卷和完整安装包摘要。
- 发布前通过 Gitee 远端 Git tag 核对源提交。恢复流程使用专用 `GITEE_RELEASE_TARGET_COMMITISH` 变量，避免覆盖 GitHub 保留变量 `GITHUB_SHA`。
- Gitee 更新既有 Release 说明时同时提交必需的 `tag_name`、`name` 和 `body`。
- `node --test scripts/publish-github-release.test.cjs scripts/publish-gitee-release.test.cjs`：24 项通过。Windows Actions 分卷辅助脚本测试通过，损坏分卷被拒绝，失败输出已删除。

## 实机测试边界

CI 已验证 Windows 安装包构建、既有安装/卸载检查，以及分卷还原脚本；这不替代教师机和学生机的真实机房验收。安装包未进行 Authenticode 签名，仍需按[1.0.3 Beta 实机测试指南](../../BETA-TESTING.md)在可恢复的 Windows 测试电脑上检查，并反馈真实安装、网站限制和 Veyon 配置结果。

## 2026-10-13 下载方式简化

两个安装包分别为 145,444,177 bytes 和 127,842,564 bytes，超过 Gitee 单附件上限。发布页现在按角色直接链接至 GitHub Release 上对应的完整 EXE；教师和学生仍分别下载各自的一个安装包。后续版本会沿用此方式。
