#!/usr/bin/env bash
set -euo pipefail
set +x

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
secret_file="$repo_root/.env.cloudbase.local"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  printf '用法：bash scripts/run-live-package-api-e2e.sh --confirm-live-synthetic-test\n'
  printf '脚本会登录 CloudBase Auth、先验证管理员撤回权限，再发布并清理一个随机 schema v5 合成配置包。\n'
  exit 0
fi

if [[ "$#" -ne 1 || "$1" != "--confirm-live-synthetic-test" ]]; then
  printf '拒绝启动线上合成写入测试。\n' >&2
  printf '若已确认使用共享体验环境并允许发布后撤回，请传入 --confirm-live-synthetic-test。\n' >&2
  exit 2
fi

if [[ ! -f "$secret_file" ]]; then
  printf '缺少本机受保护配置：.env.cloudbase.local。没有登录 CloudBase 或访问线上 API。\n' >&2
  exit 2
fi

# This ignored local file contains the server-only service API key. Never enable shell tracing here.
set -a
source "$secret_file"
set +a

if [[ "${CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW:-}" != "yes" ]]; then
  printf '服务 API key 轮换标记不符合测试门禁，已停止。\n' >&2
  unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
  exit 2
fi
if [[ -z "${CLOUDBASE_SERVICE_ROLE_KEY:-}" ]]; then
  printf '配置文件中缺少 CloudBase 服务 API key，已停止。\n' >&2
  unset TELEMETRY_DAILY_HASH_KEY
  exit 2
fi

target_env_id="$(node -e 'const fs=require("node:fs"); const config=JSON.parse(fs.readFileSync(process.argv[1],"utf8")); process.stdout.write(config.envId || "");' "$repo_root/cloudbaserc.json")"
if [[ "$target_env_id" != "veyon-control-d3gs8hmuyd09c00a7" ]]; then
  printf 'cloudbaserc.json 目标环境不是已授权的共享体验环境，已停止。\n' >&2
  unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
  exit 2
fi

admin_bearer_token="${VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN:-}"
if [[ -z "$admin_bearer_token" ]]; then
  if [[ ! -r /dev/tty ]]; then
    printf '需要交互式终端登录 CloudBase Auth；请在本机终端运行此脚本。没有读取凭据。\n' >&2
    unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
    exit 2
  fi
  username=''
  password=''
  printf 'CloudBase 管理员用户名：' >/dev/tty
  IFS= read -r username </dev/tty
  printf 'CloudBase 管理员密码（输入不显示）：' >/dev/tty
  IFS= read -r -s password </dev/tty
  printf '\n' >/dev/tty
  if [[ -z "$username" || -z "$password" ]]; then
    printf '用户名和密码不能为空，已停止。\n' >&2
    unset username password CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
    exit 2
  fi
  if ! admin_bearer_token="$(printf '%s\n%s\n' "$username" "$password" | \
    node "$repo_root/scripts/mint-cloudbase-admin-test-token.cjs" "$target_env_id")"; then
    unset username password admin_bearer_token CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
    exit 2
  fi
  unset username password
fi

admin_bearer_token="${admin_bearer_token#Bearer }"
if [[ -z "$admin_bearer_token" || "$admin_bearer_token" == *[[:space:]]* ]]; then
  printf '管理员访问令牌格式无效，已停止。\n' >&2
  unset admin_bearer_token CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
  exit 2
fi

export CloudBase__EnvId="$target_env_id"
export CloudBase__ApiKey="$CLOUDBASE_SERVICE_ROLE_KEY"
export CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes
export VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN="$admin_bearer_token"
unset admin_bearer_token CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY target_env_id
trap 'unset CloudBase__EnvId CloudBase__ApiKey CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW VEYONCAMPUS_LIVE_TEST_ADMIN_BEARER_TOKEN' EXIT

cd "$repo_root"
npm run check:live --prefix cloudfunctions/veyon-api -- --confirm-live-synthetic-test
