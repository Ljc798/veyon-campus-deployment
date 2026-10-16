# 手机课堂屏幕巡视：API 契约

本文记录教师端 Mobile API 当前实现的屏幕预览接口，供手机页面与后续兼容性检查使用。该接口只在已配对手机连接教师电脑 HTTPS 服务时开放；教师服务只接受教师电脑所在校园 LAN 的请求。

## 通用认证

本契约中的手机会话和屏幕巡视接口请求都携带：

```http
Authorization: Bearer <已配对手机令牌>
X-Veyon-Request-Nonce: <本次请求唯一的 32 位十六进制 GUID>
X-Veyon-Request-Timestamp: <UTC ISO 8601 时间>
```

随机数不能重用；时间戳允许范围为教师机当前 UTC 时间前 2 分钟至后 1 分钟。图片读取还必须带当前巡视租约：

```http
X-Veyon-Screen-Preview-Lease: <32 位十六进制 GUID>
```

每个租约只属于启动它的配对手机和活动课堂 Session。租约 60 秒没有有效画面请求会过期；有效请求会续期。停止巡视、重新启动、配对撤销、课堂或座位关联变化、教师服务关闭都会撤销租约并取消进行中的采集。

## 获取当前课堂屏幕目标

`GET /api/session` 返回的 `activeClassroomScreenTargets` 是当前课堂已关联的可巡视座位。每项结构如下：

```json
{
  "targetId": "32 位十六进制 GUID",
  "seatNumber": 12,
  "row": 2,
  "column": 4,
  "studentName": "学生姓名",
  "computerName": "电脑名称"
}
```

屏幕目标条目不包含采集用主机地址。手机按目标 ID 请求，教师端会在每次打开租约及读取图片时，从当前活动课堂和座位关联重新解析目标。`/api/session` 的其他既有字段服务于其他课堂功能，不能据本字段推断整个响应都不含目标地址。

## 启动普通单帧巡视

`POST /api/classroom/screen-preview/start`

```json
{
  "targets": ["32 位十六进制 GUID"],
  "quality": "low",
  "refreshIntervalSeconds": 10
}
```

- `targets` 可选；提供时须为 1–150 个不重复的当前课堂目标 ID。手机页面提交本堂课全部已关联座位；省略时服务端选择全部已关联座位（最多 150 个）。扩展租约范围不取消设备/全局速率限制、两路并发、缓存和图片尺寸限制。
- `quality`：`low`、`standard`、`high`。省略或未知值回退到 `low`。
- `refreshIntervalSeconds`：服务端限制到 3–60 秒，并归一到最近的 `3 / 5 / 10 / 15 / 30 / 60` 秒档位；省略时使用 10 秒。
- 一部手机同时只有一个巡视租约；再次启动会撤销该手机之前的租约。

成功响应：

```json
{
  "leaseId": "租约 GUID",
  "expiresUtc": "2026-10-17T08:00:00Z",
  "sessionId": "课堂 Session GUID",
  "targets": ["00112233445566778899aabbccddeeff", "ffeeddccbbaa99887766554433221100"],
  "totalTargets": 2,
  "quality": "low",
  "refreshIntervalSeconds": 10
}
```

`totalTargets` 是当前可关联座位总数；`targets` 是本租约允许读取的目标。手机端普通巡视提交全部当前座位，因此这两个数量相同。

## 读取普通巡视画面

`GET /api/classroom/screen-preview/{targetId}`，并附上通用认证头和当前租约头。

仅当目标仍属于当前活动课堂、包含在该手机的当前租约内且已达到租约刷新间隔时才返回画面。每个目标截图至少间隔 3 秒；手机每分钟最多 120 次、教师服务全局最多 600 次屏幕请求；Veyon 采集最多同时运行 2 路。

成功响应为 `image/jpeg`，带有：

- `Cache-Control: no-store, no-cache, max-age=0`
- `Pragma: no-cache`、`Expires: 0`
- `X-Captured-Utc`：画面采集时间（UTC）
- `X-Screen-Frame`：`captured` 表示新采集，`cached` 表示复用最低间隔内的母图
- `X-Screen-Quality`：实际画质档位

原始 PNG 输入限制为 32 MiB 和 5000 万像素；教师端将画面缩至不超过 1280×720，编码为不超过 1 MiB 的 JPEG。普通巡视的输出档位上限为：

| 档位 | 最大尺寸 | JPEG 质量值 |
| --- | --- | ---: |
| `low`（省流） | 320×180 | 48 |
| `standard`（标准） | 640×360 | 68 |
| `high`（清晰） | 960×540 | 84 |

输出保持原图宽高比，尺寸不会超过对应上限。母图只放在教师端内存中，课堂缓存上限 10 MiB、缓存最长 5 秒；Service Worker 绕过所有 `/api/` 路径。

## 停止巡视

`POST /api/classroom/screen-preview/stop`

```json
{ "leaseId": "租约 GUID" }
```

响应：`{ "stopped": true }` 或 `{ "stopped": false }`。该端点也用于停止手机 Veyon 实时画面租约。

## Veyon 手机实时画面

该模式仅能查看一个座位，与普通巡视的画质档位及刷新间隔分开：

1. `POST /api/classroom/veyon-view/mobile/start`，JSON `{ "target": "目标 GUID" }`。教师端在本机打开对应 Veyon 监看窗口并返回 `target`、`leaseId`、`sessionId`、`message`。
2. 按约 1 秒间隔调用 `GET /api/classroom/veyon-view/mobile/{targetId}`，附上通用认证头和当前租约头。成功响应为不缓存的 `image/jpeg`，带 `X-Captured-Utc`。
3. 以 `/api/classroom/screen-preview/stop` 撤销租约并关闭本机 Veyon 监看窗口。

`POST /api/classroom/veyon-view/open`，JSON `{ "target": "目标 GUID" }`，只在教师电脑打开 Veyon 窗口，不建立手机画面传输。以上 Veyon 窗口接口仅在 Windows 教师端可用。

## 错误响应

错误 JSON 使用 `error` 字段。常见 HTTP 状态：`400` 输入无效、`401` 配对/随机数/课堂/租约无效、`429` 请求过快（含 `Retry-After: 60`）；Veyon 启动冲突可能返回 `409`，非 Windows 教师端不支持 Veyon 窗口接口并返回 `501`，未处理的教师端操作错误返回 `500`。租约或课堂变化后，客户端应刷新 `/api/session` 并由教师重新打开巡视。

## 隐私与审计边界

- 浏览器只提交目标 GUID，不提交主机名或 IP；教师端始终根据当前课堂重新解析主机。
- Veyon 原始 PNG 会暂存于教师机 Veyon 截图目录；只有唯一匹配本次请求并通过文件指纹校验的文件才会删除，无法安全确认时会保留。缩放后的 JPEG 只在教师端内存短暂缓存并经当前 HTTPS 请求传输，不录制、不上传 CloudBase。
- 审计记录座位标签和操作结果，不记录目标 GUID、主机地址、图片内容或屏幕文字。
