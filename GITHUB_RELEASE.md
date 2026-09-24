# GitHub 发布指南

目标仓库：https://github.com/Ljc798/veyon-campus-deployment

## 直接推送代码与教程

本项目把脚本、文档和 `视频教程/` 下的四段成片直接放到仓库。Release 可作为版本归档使用，不是观看视频的必要步骤。

`.gitignore` 排除本机杂项、安装程序、压缩包、真实部署配置与密钥，以及根目录的原始录像 `一键导入计算机.mp4`；教程目录中的成片可以正常提交。原录像仍保留在本机。

在本目录执行：

```sh
git status --short
git add .
git diff --cached --stat
git diff --cached --name-only
```

核对包含 `add_computer_v1.txt`、更新后的文档和第 04 段成片，不包含真实部署数据及原录像，然后提交推送：

```sh
git commit -m "Document bulk computer import and add annotated tutorial"
git push
```

如果远端已有新提交，先同步，不要强制覆盖。网页上传时 `.gitignore` 不会自动筛选文件，请选择需要公开的文件。

## 可选：创建 Release

需要为某个提交建立版本发布页时，在仓库 Releases 中选择对应提交、填写新标签和版本说明即可。已经存在的发布标签不要重复用于不同内容。视频已在代码仓库中，无需再次作为附件上传。

安装程序请从 Veyon 官网获取。公开可见不等于开源授权；项目尚未指定许可证，由权利人决定后再添加 `LICENSE`。
