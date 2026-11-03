#!/usr/bin/env bash
set -euo pipefail
set +x
umask 077

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
known_production_env_id='veyon-control-d3gs8hmuyd09c00a7'
mode=''
env_id=''
confirmed_env_id=''
confirmed_migrations=''

usage() {
  cat <<'USAGE'
用法：
  bash scripts/apply-cloudbase-release-migrations.sh \
    --env-id <隔离测试环境ID> --confirm-isolated-env <同一环境ID> --dry-run

  bash scripts/apply-cloudbase-release-migrations.sh \
    --env-id <隔离测试环境ID> --confirm-isolated-env <同一环境ID> \
    --apply --confirm-migration-ids 20261021140000,20261102120000

脚本只选择以下两条迁移，不会扫描仓库的完整 migration 目录：
  20261021140000_create_file_update_release_catalog
  20261102120000_support_signed_student_agent_targets

默认不得对 production 执行。应用模式会先做只读预演，再按 allowlist 调用 CloudBase CLI；
请只对可丢弃或可恢复的隔离测试环境使用。
USAGE
}

fail() {
  printf '迁移已停止：%s\n' "$1" >&2
  exit 2
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --env-id)
      [[ "$#" -ge 2 ]] || fail '--env-id 缺少参数'
      env_id="$2"
      shift 2
      ;;
    --confirm-isolated-env)
      [[ "$#" -ge 2 ]] || fail '--confirm-isolated-env 缺少参数'
      confirmed_env_id="$2"
      shift 2
      ;;
    --confirm-migration-ids)
      [[ "$#" -ge 2 ]] || fail '--confirm-migration-ids 缺少参数'
      confirmed_migrations="$2"
      shift 2
      ;;
    --dry-run)
      [[ -z "$mode" ]] || fail '只能指定一次 --dry-run 或 --apply'
      mode='dry-run'
      shift
      ;;
    --apply)
      [[ -z "$mode" ]] || fail '只能指定一次 --dry-run 或 --apply'
      mode='apply'
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      usage >&2
      fail "未知参数：$1"
      ;;
  esac
done

[[ -n "$env_id" ]] || fail '必须显式提供隔离测试环境 ID'
[[ -n "$mode" ]] || fail '必须选择 --dry-run 或 --apply'
[[ "$confirmed_env_id" == "$env_id" ]] || fail '请用 --confirm-isolated-env 再次输入同一个隔离环境 ID'

if [[ "$mode" == 'apply' ]]; then
  [[ "$confirmed_migrations" == '20261021140000,20261102120000' ]] ||
    fail '应用模式必须显式确认完整 migration allowlist：20261021140000,20261102120000'
elif [[ -n "$confirmed_migrations" ]]; then
  fail '--confirm-migration-ids 只适用于 --apply'
fi

[[ "$env_id" =~ ^[A-Za-z0-9-]{6,64}$ ]] || fail '环境 ID 格式不合法'

tcb_cli="$(command -v tcb || true)"
[[ -n "$tcb_cli" ]] || fail '找不到 CloudBase CLI（tcb）'
node_cli="$(command -v node || true)"
[[ -n "$node_cli" ]] || fail '找不到 Node.js（用于安全生成临时 CLI 配置和校验预演结果）'
production_env_id="$("$node_cli" -e 'const fs=require("node:fs"); const config=JSON.parse(fs.readFileSync(process.argv[1], "utf8")); process.stdout.write(config.envId || "");' "$repo_root/cloudbaserc.json")" ||
  fail '无法读取仓库配置中的 production envId'
[[ -n "$production_env_id" ]] || fail '仓库 cloudbaserc.json 未设置 production envId'
[[ "$env_id" != "$known_production_env_id" ]] || fail '目标是已核实的 production 环境，脚本拒绝执行'
[[ "$env_id" != "$production_env_id" ]] || fail '目标是仓库配置的 production 环境，脚本拒绝执行'

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/veyon-release-migrations.XXXXXX")" ||
  fail '无法创建临时迁移目录'
trap 'rm -rf "$tmp_dir"' EXIT

migration_dir="$tmp_dir/migrations"
config_file="$tmp_dir/cloudbaserc.json"
mkdir -m 700 "$migration_dir"

migration_files=(
  '20261021140000_create_file_update_release_catalog.sql'
  '20261102120000_support_signed_student_agent_targets.sql'
)
for migration_file in "${migration_files[@]}"; do
  source_file="$repo_root/cloudbase/migrations/$migration_file"
  [[ -f "$source_file" && ! -L "$source_file" ]] ||
    fail "迁移源文件不存在或不是普通文件：$migration_file"
  cp "$source_file" "$migration_dir/$migration_file"
done

MIGRATION_DIR="$migration_dir" CONFIG_FILE="$config_file" "$node_cli" <<'NODE'
const fs = require('node:fs');
const path = require('node:path');
const migrationDir = process.env.MIGRATION_DIR;
const configFile = process.env.CONFIG_FILE;
if (!migrationDir || !path.isAbsolute(migrationDir) || !configFile || !path.isAbsolute(configFile)) {
  throw new Error('temporary migration configuration paths must be absolute');
}
fs.writeFileSync(configFile, JSON.stringify({
  version: '2.1',
  database: { migrations: [migrationDir] }
}, null, 2), { mode: 0o600 });
NODE

remote_file="$tmp_dir/remote-migrations.json"
if ! remote_output="$(env -u TCB_TCR_USERNAME -u TCB_TCR_PASSWORD \
    "$tcb_cli" --config-file "$config_file" db pg migration list \
    -e "$env_id" --remote-only --json)"; then
  fail '无法读取测试环境 migration ledger；没有提交迁移'
fi
printf '%s\n' "$remote_output" > "$remote_file"

if ! REMOTE_FILE="$remote_file" "$node_cli" <<'NODE'
const fs = require('node:fs');
const report = JSON.parse(fs.readFileSync(process.env.REMOTE_FILE, 'utf8'));
const items = report.remote?.items;
if (!Array.isArray(items)) throw new Error('remote migration ledger response is invalid');
if (Number(report.remote.total) !== items.length) throw new Error('remote migration ledger is incomplete');
const applied = new Map(items.map((item) => [String(item.version ?? ''), String(item.name ?? '')]));
if (applied.size !== items.length) throw new Error('remote migration ledger contains duplicate version IDs');
const requiredBaseline = new Map([
  ['20260930150000', 'create_application_releases_and_teacher_heartbeat'],
  ['20261001090000', 'align_package_prefix_validation'],
  ['20261001090001', 'add_shared_package_download_rate_limit'],
  ['20261001100000', 'name_deployment_packages_by_campus'],
  ['20261006100000', 'support_student_system_policy_packages'],
  ['20261006110000', 'support_release_policy_capabilities'],
  ['20261006120000', 'support_application_policy_release_capability'],
  ['20261008100000', 'support_package_setup_recommendations'],
  ['20261016120000', 'add_deployment_package_fingerprint_key_version']
]);
const missing = [...requiredBaseline.keys()].filter((version) => !applied.has(version));
if (missing.length) throw new Error(`test environment is missing required baseline migrations: ${missing.join(',')}`);
for (const [version, name] of requiredBaseline) {
  if (applied.get(version) !== name) throw new Error(`test environment baseline name mismatch for ${version}`);
}
if (applied.has('20261010090000')) {
  throw new Error('test environment already contains the disabled OperationsReporting migration; use a clean isolated baseline');
}
const expectedNames = new Map([
  ['20261021140000', 'create_file_update_release_catalog'],
  ['20261102120000', 'support_signed_student_agent_targets']
]);
for (const [version, name] of expectedNames) {
  if (applied.has(version) && applied.get(version) !== name) {
    throw new Error(`remote migration name mismatch for ${version}`);
  }
}
process.stdout.write(`Baseline migration ledger verified (${items.length} applied entries).\n`);
NODE
then
  fail '测试环境迁移基线不符合预期；没有提交迁移'
fi

preflight_file="$tmp_dir/preflight.json"
if ! preflight_output="$(env -u TCB_TCR_USERNAME -u TCB_TCR_PASSWORD \
    "$tcb_cli" --config-file "$config_file" db pg migration up \
    -e "$env_id" --dry-run --json)"; then
  fail 'CloudBase 只读预演失败；没有提交迁移'
fi
printf '%s\n' "$preflight_output" > "$preflight_file"

if ! pending_ids="$(EXPECTED_ENV_ID="$env_id" PREFLIGHT_FILE="$preflight_file" "$node_cli" <<'NODE'
const fs = require('node:fs');
const expectedEnvId = process.env.EXPECTED_ENV_ID;
const report = JSON.parse(fs.readFileSync(process.env.PREFLIGHT_FILE, 'utf8'));
if (!Array.isArray(report.pending) || !Array.isArray(report.conflicts)) {
  throw new Error('preflight pending/conflicts shape is invalid');
}
const pending = report.pending;
const conflicts = report.conflicts;
const allowed = new Map([
  ['20261021140000', 'create_file_update_release_catalog'],
  ['20261102120000', 'support_signed_student_agent_targets']
]);
const read = (item, lower, upper) => item[lower] ?? item[upper];
if (report.envId !== expectedEnvId) throw new Error('preflight returned a different environment ID');
if (report.dryRun !== true || report.includeAll !== false) throw new Error('preflight did not use the expected safe mode');
if (conflicts.length !== 0) throw new Error(`preflight has ${conflicts.length} migration conflict(s)`);
if (report.executable !== true) throw new Error('preflight says the plan is not executable');
for (const item of pending) {
  const version = String(read(item, 'version', 'Version') ?? '');
  const name = String(read(item, 'name', 'Name') ?? '');
  if (allowed.get(version) !== name) throw new Error(`unexpected pending migration: ${version}_${name}`);
}
const versions = pending.map((item) => String(read(item, 'version', 'Version')));
if (new Set(versions).size !== versions.length || versions.join(',') !== [...versions].sort().join(',')) {
  throw new Error('preflight migration order is invalid');
}
process.stdout.write(versions.join(','));
NODE
)"; then
  printf '%s\n' "$preflight_output"
  fail '预演结果超出 allowlist、有冲突或不可执行；没有提交迁移'
fi

printf '目标 CloudBase 环境：%s\n' "$env_id"
printf '预演结果（仅允许两条目标迁移）：\n%s\n' "$preflight_output"

if [[ "$mode" == 'dry-run' ]]; then
  printf '只读预演结束；没有应用 migration。\n'
  exit 0
fi

if [[ -z "$pending_ids" ]]; then
  if ! REQUIRE_TARGETS=true REMOTE_FILE="$remote_file" "$node_cli" <<'NODE'
const fs = require('node:fs');
const report = JSON.parse(fs.readFileSync(process.env.REMOTE_FILE, 'utf8'));
const items = report.remote?.items;
if (!Array.isArray(items)) throw new Error('remote migration ledger response is invalid');
if (Number(report.remote.total) !== items.length) throw new Error('remote migration ledger is incomplete');
const applied = new Map(items.map((item) => [String(item.version ?? ''), String(item.name ?? '')]));
if (applied.size !== items.length) throw new Error('remote migration ledger contains duplicate version IDs');
const expected = new Map([
  ['20261021140000', 'create_file_update_release_catalog'],
  ['20261102120000', 'support_signed_student_agent_targets']
]);
for (const [version, name] of expected) {
  if (applied.get(version) !== name) throw new Error('remote ledger is missing target migration ' + version);
}
NODE
  then
    fail '预演显示无待应用迁移，但远端 ledger 没有记录完整目标版本'
  fi
  printf '两条 allowlist migration 均已应用；无需写入。\n'
  exit 0
fi

printf '本次待应用 migration ID：%s\n' "$pending_ids"
if ! env -u TCB_TCR_USERNAME -u TCB_TCR_PASSWORD \
    "$tcb_cli" --config-file "$config_file" db pg migration up \
    -e "$env_id" --json; then
  fail 'CloudBase migration 执行失败；保留 CLI 输出供排查，先查询远端 ledger 再决定是否重试'
fi

post_apply_file="$tmp_dir/post-apply-migrations.json"
if ! post_apply_output="$(env -u TCB_TCR_USERNAME -u TCB_TCR_PASSWORD \
    "$tcb_cli" --config-file "$config_file" db pg migration list \
    -e "$env_id" --remote-only --json)"; then
  fail '迁移命令返回成功，但无法读回远端 ledger；请先人工核对状态再重试'
fi
printf '%s\n' "$post_apply_output" > "$post_apply_file"
if ! REQUIRE_TARGETS=true REMOTE_FILE="$post_apply_file" "$node_cli" <<'NODE'
const fs = require('node:fs');
const report = JSON.parse(fs.readFileSync(process.env.REMOTE_FILE, 'utf8'));
const items = report.remote?.items;
if (!Array.isArray(items)) throw new Error('remote migration ledger response is invalid');
if (Number(report.remote.total) !== items.length) throw new Error('remote migration ledger is incomplete');
const applied = new Map(items.map((item) => [String(item.version ?? ''), String(item.name ?? '')]));
if (applied.size !== items.length) throw new Error('remote migration ledger contains duplicate version IDs');
const expected = new Map([
  ['20261021140000', 'create_file_update_release_catalog'],
  ['20261102120000', 'support_signed_student_agent_targets']
]);
if (applied.has('20261010090000')) {
  throw new Error('disabled OperationsReporting migration appeared in the remote ledger');
}
for (const [version, name] of expected) {
  if (applied.get(version) !== name) throw new Error('remote ledger is missing target migration ' + version);
}
NODE
then
  fail '迁移命令返回成功，但目标 migration ledger 读回不完整；请先人工核对状态再重试'
fi
printf '目标 migration 已从远端 ledger 读回确认。\n'
