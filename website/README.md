# Veyon Campus 网站

网站与 API 的统一开发、部署及验收入口已合并到[网站与云端指南](../docs/网站与云端指南.md)。

- 前端专用约束：[开发规范](docs/开发规范.md)
- 数据库表与权限：[PostgreSQL 数据库设计](docs/PostgreSQL数据库设计.md)
- 项目总进度：[任务主表](../docs/开发路线与任务清单.md)

本目录不再重复维护云端资源状态和发布进度。Teacher App 发布部署包无需教师账号或登录，只填写校区名称、教师姓名和手机号后四位。网站 `/admin` 登录仅用于站点数据管理，不控制教师上传。线上迁移、API 部署和端到端状态以[网站与云端指南](../docs/网站与云端指南.md)为准。

## 文档中心本地评测

`npm run build` 会生成公开文章和 Pagefind 静态索引。需要评估 47 条自然语言查询时，先完成构建，再运行 `npm run prepare:search-evaluation` 和 `npm run preview`，打开 `http://127.0.0.1:4173/docs/__search-evaluation.local.html`。评测页通过本地 `/docs` 搜索统计前 3 名命中率、关键问题命中率、查询耗时和 Pagefind 资源估算；完成后可下载 JSON 记录保存在本机，不会上传。评测脚本只会在 `localhost`、`127.0.0.1` 或 `::1` 主机运行。评测页不在常规构建中生成；评测完成后先停止 Preview，再运行 `npm run clean:search-evaluation`，再执行其他发布操作。
