# GitHub 发布指南

目标仓库：https://github.com/Ljc798/veyon-campus-deployment

## 仓库内容

提交脚本、Markdown 文档、文本说明、校验清单和 `.github` 模板。`.gitignore` 已排除本机杂项、安装程序、视频、压缩包、真实部署配置与密钥；本地原文件仍保留。使用网页上传时忽略规则不会替你筛选文件，请勿把整个本地文件夹拖入上传页面。

视频通过 Release 附件分发，安装程序通过 Veyon 官网获取。参见 [GitHub 大文件说明](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github)。

## 提交源码

在本目录打开终端，依次执行并检查输出：

```sh
git status --short
git add .
git diff --cached --stat
git diff --cached --name-only
```

确认暂存文件不包含真实部署数据或媒体文件后：

```sh
git commit -m "Prepare v1.0 public release"
git branch -M main
git push -u origin main
```

如果远端已有提交，先拉取并核对差异，不要强制推送覆盖。公开仓库不等同于开源授权；如需开源，请由权利人选定许可证后添加 `LICENSE`。

## 创建 v1.0 Release

1. 打开仓库 Releases，创建新 Release，目标选择已推送的提交，标签填写 `v1.0`。
2. 标题建议：`v1.0 · 首次公开学习版`；说明使用 `发布说明.md`。
3. 上传前审查三段录像中的凭据和个人信息；本次仓库整理没有重新逐帧审查视频。
4. 上传本地 `视频教程/` 下的三段 MP4 和 `SHA256SUMS.txt`。不上传安装程序或实际部署包。
5. 检查附件名称与校验值，再发布 Release。

GitHub 自动生成的源码压缩包只包含提交到仓库的内容，不包含被忽略的视频和安装程序。
