#!/usr/bin/env bash
set -euo pipefail
set +x

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
secret_file="$repo_root/.env.cloudbase.local"
domestic_dns_wrapper="$repo_root/scripts/with-domestic-dns.sh"

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  printf '用法：bash scripts/deploy-cloudbase-api.sh --confirm-public-api\n'
  printf '该脚本会把本地 Node.js 代码包部署为公网 HTTP 云函数 veyon-api。\n'
  exit 0
fi

if [[ "$#" -ne 1 || "$1" != "--confirm-public-api" ]]; then
  printf '拒绝部署：该目标会创建公网可访问的 API。\n' >&2
  printf '请先完成 CloudBase 部署门禁确认，再显式传入 --confirm-public-api。\n' >&2
  printf '没有读取本地密钥、联系 CloudBase 或修改云端资源。\n' >&2
  exit 2
fi

if [[ ! -f "$domestic_dns_wrapper" ]]; then
  printf '缺少国内站 DNS 包装器：%s\n' "$domestic_dns_wrapper" >&2
  printf '部署已停止；没有开始构建或修改云端资源。\n' >&2
  exit 2
fi

if [[ "${CLOUDBASE_SERVICE_ROLE_KEY_ROTATED:-}" != "yes" ]]; then
  if [[ ! -t 0 ]]; then
    printf '部署前请先轮换此前出现在工具输出中的 service API key，并更新 .env.cloudbase.local。\n' >&2
    printf '轮换完成后，在交互终端运行脚本，或安全设置 CLOUDBASE_SERVICE_ROLE_KEY_ROTATED=yes。\n' >&2
    printf '脚本没有读取本地密钥文件，也没有修改云端资源。\n' >&2
    exit 2
  fi
  read -r -p '确认已撤销旧 service API key 并更新本地密钥？输入 yes 继续: ' rotation_confirmed
  if [[ "$rotation_confirmed" != "yes" ]]; then
    printf '请先轮换密钥。脚本没有读取本地密钥文件，也没有修改云端资源。\n' >&2
    exit 2
  fi
fi

if [[ -f "$secret_file" ]]; then
  set -a
  # This ignored local file contains server-only keys. Never enable shell tracing here.
  source "$secret_file"
  set +a
fi

missing=0
for required_name in \
  CLOUDBASE_SERVICE_ROLE_KEY \
  TELEMETRY_DAILY_HASH_KEY; do
  if [[ -z "${!required_name:-}" ]]; then
    printf '缺少部署变量：%s\n' "$required_name" >&2
    missing=1
  fi
done

if [[ "$missing" -ne 0 ]]; then
  printf '\n服务密钥应保存在 .env.cloudbase.local。\n' >&2
  printf '请配置全部变量后重试。脚本没有开始构建或修改云端资源。\n' >&2
  exit 2
fi

if [[ "${CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW:-}" != "yes" ]]; then
  printf '部署前请轮换本地服务密钥文件中的 service API key，并设置 CLOUDBASE_SERVICE_ROLE_KEY_ROTATED_AFTER_20260930_REVIEW=yes。\n' >&2
  printf '没有开始构建或修改云端资源。\n' >&2
  exit 2
fi

tcb_cli="$(command -v tcb || true)"
if [[ -z "$tcb_cli" ]]; then
  for candidate in "$HOME"/.npm/_npx/*/node_modules/.bin/tcb; do
    if [[ -x "$candidate" ]]; then
      tcb_cli="$candidate"
      break
    fi
  done
fi

if [[ -z "$tcb_cli" ]]; then
  printf '找不到 CloudBase CLI（tcb）。请安装 CLI 后重试。\n' >&2
  exit 2
fi

if ! site_is_intl="$(bash "$domestic_dns_wrapper" "$tcb_cli" config get isIntl 2>/dev/null | tail -n 1)" ||
   [[ "$site_is_intl" != "false" ]]; then
  printf '当前 CloudBase CLI 不是已确认的国内站（isIntl=false），已停止部署。\n' >&2
  printf '请在本机检查 tcb 国内站配置；没有开始构建或修改云端资源。\n' >&2
  exit 2
fi

if ! remote_migrations="$(env -u TCB_TCR_USERNAME -u TCB_TCR_PASSWORD bash "$domestic_dns_wrapper" "$tcb_cli" db pg migration list \
  -e veyon-control-d3gs8hmuyd09c00a7 --remote-only --json 2>&1)"; then
  printf '无法读取 CloudBase 远端 PostgreSQL 迁移历史，已停止部署。请恢复 CLI 登录和网络后重试。\n' >&2
  unset remote_migrations CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
  exit 2
fi
# OperationsReporting remains disabled; its old 20261010090000 migration is behind
# the remote ledger and must not block deployment of the package API.
for required_version in \
  20260930150000 \
  20261001090000 \
  20261001090001 \
  20261001100000 \
  20261006100000 \
  20261006110000 \
  20261006120000 \
  20261008100000 \
  20261016120000 \
  20261021140000; do
  if ! printf '%s\n' "$remote_migrations" | grep -Eq '"version"[[:space:]]*:[[:space:]]*"'"$required_version"'"'; then
    printf '远端尚未确认应用迁移 %s，已停止部署。请先预览并按依赖顺序应用所需数据库迁移。\n' "$required_version" >&2
    unset remote_migrations CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
    exit 2
  fi
done
unset remote_migrations

cd "$repo_root"
if ! node -e '
  const fs = require("node:fs");
  const config = JSON.parse(fs.readFileSync("cloudbaserc.json", "utf8"));
  const fn = (config.functions || []).find((item) => item.name === "veyon-api");
  if (config.envId !== "veyon-control-d3gs8hmuyd09c00a7" ||
      config.region !== "ap-shanghai" ||
      !fn || fn.runtime !== "Nodejs20.19" ||
      fn.dir !== "cloudfunctions/veyon-api" ||
      fn.envVariables?.DeploymentPackages__FingerprintKeyRingBase64 !== "{{env.DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING_BASE64}}" ||
      fn.envVariables?.DeploymentPackages__ActiveFingerprintKeyId !== "{{env.DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID}}" ||
      fn.envVariables?.OperationsReporting__Enabled !== "false") process.exit(1);
'; then
  printf 'cloudbaserc.json 的环境、地域或函数配置与本次部署目标不符，已停止。\n' >&2
  unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
  exit 2
fi

if [[ ! -x "$repo_root/cloudfunctions/veyon-api/scf_bootstrap" ||
      ! -f "$repo_root/cloudfunctions/veyon-api/index.js" ||
      ! -f "$repo_root/cloudfunctions/veyon-api/package-validator.js" ]]; then
  printf 'HTTP 云函数代码包不完整或 scf_bootstrap 不可执行，已停止。\n' >&2
  unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
  exit 2
fi

if [[ -z "${DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING:-}" ]]; then
  if [[ ${#TELEMETRY_DAILY_HASH_KEY} -lt 44 ||
        ! "$TELEMETRY_DAILY_HASH_KEY" =~ ^[A-Za-z0-9+/]+={0,2}$ ]]; then
    printf 'Telemetry 日密钥格式无效，无法生成配置包指纹密钥环。\n' >&2
    unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY
    exit 2
  fi
  printf -v DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING '{"legacy":"%s"}' "$TELEMETRY_DAILY_HASH_KEY"
fi
DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID="${DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID:-legacy}"
export DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID

if ! node -e '
  const ringText = process.env.DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING || "";
  const activeKeyId = process.env.DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID || "";
  const keyIdPattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
  let ring;
  try { ring = JSON.parse(ringText); } catch { process.exit(1); }
  if (!ring || typeof ring !== "object" || Array.isArray(ring) ||
      Object.keys(ring).length < 1 || Object.keys(ring).length > 8 ||
      !keyIdPattern.test(activeKeyId) || !Object.hasOwn(ring, activeKeyId)) process.exit(1);
  for (const [keyId, encodedKey] of Object.entries(ring)) {
    if (!keyIdPattern.test(keyId) || typeof encodedKey !== "string" ||
        !/^[A-Za-z0-9+/]*={0,2}$/.test(encodedKey)) process.exit(1);
    const key = Buffer.from(encodedKey, "base64");
    if (key.length < 32 || key.toString("base64") !== encodedKey) process.exit(1);
  }
'; then
  printf '配置包指纹密钥环或活动版本 ID 无效，已停止部署。\n' >&2
  unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID
  exit 2
fi
DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING_BASE64="$(node -e 'process.stdout.write(Buffer.from(process.env.DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING, "utf8").toString("base64"))')"
export DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING_BASE64

set +e
bash "$domestic_dns_wrapper" "$tcb_cli" fn deploy veyon-api \
  --httpFn \
  --dir cloudfunctions/veyon-api \
  --runtime Nodejs20.19 \
  --deployMode zip \
  --install-dependency false \
  --force
deploy_status=$?
unset CLOUDBASE_SERVICE_ROLE_KEY TELEMETRY_DAILY_HASH_KEY DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING DEPLOYMENT_PACKAGES_FINGERPRINT_KEY_RING_BASE64 DEPLOYMENT_PACKAGES_ACTIVE_FINGERPRINT_KEY_ID TCB_TCR_PASSWORD TCB_TCR_USERNAME
exit "$deploy_status"
