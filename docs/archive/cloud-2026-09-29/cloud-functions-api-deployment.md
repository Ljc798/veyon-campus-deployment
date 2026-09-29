> 归档于 2026-09-29：历史记录，版本、命令与结论只适用于原文场景。当前工作请从 [文档索引](../../README.md) 开始。

# Cloud Functions API deployment

The heartbeat and deployment-package API use one CloudBase HTTP Cloud Function named `veyon-api`. The existing .NET 10 handlers and root `Dockerfile` are retained as a custom image, so ZIP validation, identity HMACs, PostgreSQL RPCs, private storage calls, and route contracts stay in one implementation.

The function uses the CloudBase HTTP gateway root path and listens on port `9000`. CloudBase CLI reads `public: true` and `gatewayPath: "/"` from `cloudbaserc.json`, allowing anonymous API calls and routing existing `/health` and `/v1/...` paths to the function. Administrative routes still validate the caller's CloudBase Auth bearer token.

## Current state

CloudRun `veyon-control-dev` has been deleted. The `veyon-api` function has not yet been deployed, and the HTTP gateway currently has no API route. `cloudbaserc.json` and the website's direct gateway URL are prepared locally. The API is not reachable from CloudBase until its first deployment finishes.

## Local credentials

Copy `cloudbase.env.example` to `.env` in the repository root. Fill in the TCR personal-registry username/password, the server-side CloudBase API key, and the existing stable HMAC key. Do not commit `.env` or send any secret in chat.

`TELEMETRY_DAILY_HASH_KEY` must stay stable after launch. A read-only database check on 2026-09-29 found zero deployment packages, publisher records, and rows in the new UTC+8 telemetry tables, so no current records depend on the deleted CloudRun key. If that old key cannot be recovered, a new random 32-byte key is safe for this empty starting state. A fresh key has been generated in the ignored local `.env`; never commit or send it in chat.

## Deploy from CloudBase CLI

The CLI image build uses CloudBase's cloud build strategy because the API is .NET rather than a managed Node.js function. The command follows the screenshot's HTTP-function flow and adds image mode:

```powershell
tcb login
tcb fn deploy veyon-api --env-id veyon-control-d3gs8hmuyd09c00a7 --httpFn --deployMode image
```

The first deployment also needs Cloud Functions permission to pull from the same-region TCR image repository. The image must remain Linux `amd64` and listen on `0.0.0.0:9000`; the existing root `Dockerfile` already sets that port. The function config creates the public `/` gateway route on the environment's HTTP gateway domain.

After deployment, check `GET /health`, the public package catalog, and a teacher upload/download round trip. The website calls the HTTP gateway domain directly; the API enables CORS without browser cookies. Rebuild and republish the website after changing its `VITE_API_BASE_PATH` build value to the HTTP gateway domain.

Only the compute and gateway entry point change. PostgreSQL tables, RPCs, storage bucket, and applied migrations remain in place, so this migration does not require a database migration.
