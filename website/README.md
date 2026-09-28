# Veyon Campus 网站预览

这是与桌面 App 分离的网站原型，包含产品介绍页和管理员后台演示页。所有路由共用一个站点入口，可以直接复制整个 `website/` 目录进行后续部署。

## 本地预览

需要 Python 3，无需安装第三方依赖：

```sh
cd website
python3 preview_server.py
```

浏览器打开 <http://127.0.0.1:4173>。服务器会将 `/admin` 等前端路由回退到站点入口，因此刷新子页面也能继续预览。按 `Ctrl+C` 停止。

## 页面路由

- `/` 产品首页
- `/product` 产品能力
- `/about` 关于项目
- `/docs` 使用指南
- `/contact` 联系与反馈
- `/privacy` 隐私说明
- `/admin` 后台总览
- `/admin/campuses` 校区管理
- `/admin/usage` 使用概况
- `/admin/analytics` 趋势分析
- `/admin/settings` 后台设置

本地后台使用浏览器内置演示数据。新增校区、筛选条件和设置会保存到当前浏览器的 `localStorage`，用于体验页面交互；不同浏览器间不会同步，也不会连接桌面 App 或上传数据。

## 目录结构

- `index.html`：单页应用入口
- `app.js`：页面路由、演示数据与交互
- `styles.css`：网站与后台样式
- `assets/`：网站图标
- `preview_server.py`：本地预览服务器与路由回退
- `docs/开发规范.md`：开发、内容与数据边界约定

部署到静态托管时，需要设置未知路径回退到 `index.html`，并为根路径使用 HTTPS。正式上线前要将演示数据源替换为经过身份认证和授权的服务端 API。
