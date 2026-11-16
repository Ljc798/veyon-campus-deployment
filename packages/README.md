# 配置包静态目录

这个目录就是学生端搜索配置包时读取的“云端目录”，只不过它是文件，不是数据库。

| 文件 | 作用 |
|---|---|
| `deployment-catalog.json` | 目录索引。学生端一次 GET 就能拿到全部已发布配置包，然后在本地按校区名和电脑前缀搜索。 |
| `<packageId>.zip` | 教师端生成的校区配置包原样字节，最大 64 KiB。 |

## 不要手改这些文件

用脚本发布，它会把配置包校验一遍、算出 SHA-256、更新索引：

```bash
node scripts/publish-deployment-package.cjs \
  --package-dir "D:\配置\春晓实验学校" \
  --output-dir packages \
  --mirror-owner Ljc798 --mirror-repo veyon-campus-deployment
```

撤回一个包（保留记录，客户端立即搜不到也下不了）：

```bash
node scripts/publish-deployment-package.cjs --output-dir packages --withdraw <packageId>
```

发布后提交并推送，客户端约一分钟内可见：

```bash
git add packages && git commit -m "Publish campus package" && git push
```

## 客户端怎么读

- 主源：`https://api.gitcode.com/api/v5/repos/ljc_1126/veyon-campus-deployment/raw/packages/...?ref=develop`
- 镜像：`https://raw.githubusercontent.com/Ljc798/veyon-campus-deployment/develop/packages/...`

下载回来的字节会先核对目录登记的 SHA-256 与长度，再交给配置包校验器解压；不一致就丢弃并换下一个地址。目录本身没有签名，所以客户端只允许从上面这两个托管方下载，防止被篡改的索引把设备指向别处。

## 为什么不用数据库

配置包很小、很稀疏（每校区一次），而数据库会过期、会欠费、会被隔离。放成文件以后，代价是每次发布多一次 git 提交，换来的是永远不会因为欠费导致全校装不了机。
