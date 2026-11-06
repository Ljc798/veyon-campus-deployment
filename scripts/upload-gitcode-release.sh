#!/usr/bin/env bash
#
# 把已发布的 GitHub Release 附件同步到 GitCode（macOS / Linux 版）。
# Windows 对应脚本：scripts/upload-gitcode-release.ps1
#
# 一条命令完成：校验标签 → 同步 GitCode tag → 用 gh 下载 GitHub 附件 →
# 本机核对 SHA-256 → 调用 scripts/publish-gitcode-release.cjs 上传并读回校验。
#
# 依赖：git、node、gh（brew install node gh && gh auth login）
# Token 只从环境变量或交互式隐藏输入读取，不写入仓库、脚本或日志。

set -euo pipefail

GITHUB_REPO="Ljc798/veyon-campus-deployment"
GITCODE_REMOTE_URL="https://gitcode.com/ljc_1126/veyon-campus-deployment.git"
DEFAULT_GITCODE_OWNER="ljc_1126"
DEFAULT_GITCODE_REPO="veyon-campus-deployment"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

usage() {
  cat <<'USAGE'
用法：./scripts/upload-gitcode-release.sh <标签> [选项]

把 GitHub Release 上的 Teacher/Student 安装器和 SHA256SUMS 上传到 GitCode。

参数：
  <标签>               vX.Y.Z 或 beta-vX.Y.Z，例如 v1.1.70

选项：
  --bundle-dir <目录>  下载与上传目录，默认 artifacts/gitcode-upload/<标签>
  --skip-tag-push      不检查/不推送 GitCode 的源 tag（已确认同步时使用）
  --dry-run            只做下载与校验，不读取 Token、不上传
  --verify-only        只校验本地已有附件，不访问网络、不上传
  -h, --help           显示本帮助

环境变量：
  GITCODE_TOKEN        GitCode personal access token；未设置时脚本会隐藏输入
  GITCODE_OWNER        默认 ljc_1126
  GITCODE_REPO         默认 veyon-campus-deployment

示例：
  ./scripts/upload-gitcode-release.sh v1.1.70
  ./scripts/upload-gitcode-release.sh beta-v1.1.71 --dry-run
USAGE
}

info() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

TAG=""
BUNDLE_DIR=""
SKIP_TAG_PUSH=0
DRY_RUN=0
VERIFY_ONLY=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --bundle-dir)
      [ "$#" -ge 2 ] || die "--bundle-dir 需要一个目录参数"
      BUNDLE_DIR="$2"
      shift 2
      ;;
    --skip-tag-push) SKIP_TAG_PUSH=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --verify-only) VERIFY_ONLY=1; shift ;;
    -*) die "未知选项：$1（用 --help 查看用法）" ;;
    *)
      [ -z "$TAG" ] || die "多余的参数：$1"
      TAG="$1"
      shift
      ;;
  esac
done

[ -n "$TAG" ] || { usage >&2; exit 2; }

if ! printf '%s' "$TAG" | grep -Eq '^(beta-)?v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'; then
  die "标签必须形如 vX.Y.Z 或 beta-vX.Y.Z，当前为：$TAG"
fi

VERSION="${TAG#beta-}"
VERSION="${VERSION#v}"

STUDENT_NAME="VeyonCampus-Student-Setup-$VERSION-win-x64.exe"
TEACHER_NAME="VeyonCampus-Teacher-Setup-$VERSION-win-x64.exe"
CHECKSUMS_NAME="SHA256SUMS"
NOTES_NAME="release-notes.md"

[ -n "$BUNDLE_DIR" ] || BUNDLE_DIR="$REPO_ROOT/artifacts/gitcode-upload/$TAG"
mkdir -p "$BUNDLE_DIR"
BUNDLE_DIR="$(cd "$BUNDLE_DIR" && pwd)"
STUDENT_PATH="$BUNDLE_DIR/$STUDENT_NAME"
TEACHER_PATH="$BUNDLE_DIR/$TEACHER_NAME"
CHECKSUMS_PATH="$BUNDLE_DIR/$CHECKSUMS_NAME"
NOTES_PATH="$BUNDLE_DIR/$NOTES_NAME"
RESULT_PATH="$BUNDLE_DIR/gitcode-upload-result.json"

sha256_of() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print tolower($1)}'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print tolower($1)}'
  else
    die "需要 shasum 或 sha256sum 来计算 SHA-256"
  fi
}

expected_hash_of() {
  # SHA256SUMS 每行形如 "<64 位十六进制>  <文件名>"，文件名可能带 * 或 ./ 前缀。
  awk -v name="$1" '
    NF >= 2 {
      hash = tolower($1)
      file = $2
      sub(/^\*/, "", file)
      sub(/^\.\//, "", file)
      if (file == name) { print hash; exit }
    }
  ' "$2"
}

verify_bundle() {
  [ -s "$CHECKSUMS_PATH" ] || die "缺少 ${CHECKSUMS_NAME}：${CHECKSUMS_PATH}"
  for asset in "$STUDENT_NAME" "$TEACHER_NAME"; do
    asset_path="$BUNDLE_DIR/$asset"
    [ -s "$asset_path" ] || die "缺少安装器：$asset_path"
    expected="$(expected_hash_of "$asset" "$CHECKSUMS_PATH")"
    [ -n "$expected" ] || die "$CHECKSUMS_NAME 中没有 $asset 的摘要"
    actual="$(sha256_of "$asset_path")"
    if [ "$actual" != "$expected" ]; then
      die "$asset 的 SHA-256 与 $CHECKSUMS_NAME 不一致；删除该文件后重新运行本脚本"
    fi
    info "SHA-256 校验通过：$asset"
  done
}

if [ "$VERIFY_ONLY" -eq 1 ]; then
  info "只校验本地附件：$BUNDLE_DIR"
  verify_bundle
  info "本地附件完整。"
  exit 0
fi

command -v git >/dev/null 2>&1 || die "需要 git"
command -v node >/dev/null 2>&1 || die "需要 Node.js：brew install node"
command -v gh >/dev/null 2>&1 || die "需要 GitHub CLI：brew install gh && gh auth login"

if ! gh auth status >/dev/null 2>&1; then
  die "GitHub CLI 尚未登录：先运行 gh auth login"
fi

info "目标版本：${TAG}（${VERSION}）"

# 1. 本地标签
if git -C "$REPO_ROOT" rev-parse -q --verify "refs/tags/$TAG" >/dev/null 2>&1; then
  info "本地已有标签 $TAG"
else
  info "从 origin 获取标签 $TAG ..."
  git -C "$REPO_ROOT" fetch --no-tags origin "refs/tags/$TAG:refs/tags/$TAG"
fi
LOCAL_SHA="$(git -C "$REPO_ROOT" rev-list -n 1 "$TAG")"

# 2. 与 GitHub 上的提交核对，避免把过期本地标签推到 GitCode
REMOTE_SHA="$(gh api "repos/$GITHUB_REPO/commits/$TAG" --jq '.sha' 2>/dev/null || true)"
if [ -z "$REMOTE_SHA" ]; then
  die "GitHub 上找不到标签 ${TAG}；先确认 Release 已发布"
fi
if [ "$LOCAL_SHA" != "$REMOTE_SHA" ]; then
  die "本地标签 ${TAG} 指向 ${LOCAL_SHA}，GitHub 指向 ${REMOTE_SHA}；先更新本地标签再重试"
fi
info "源提交：$LOCAL_SHA"

# 3. GitCode 源 tag
if [ "$SKIP_TAG_PUSH" -eq 0 ]; then
  if ! git -C "$REPO_ROOT" remote get-url gitcode >/dev/null 2>&1; then
    info "添加 gitcode 远端 ..."
    git -C "$REPO_ROOT" remote add gitcode "$GITCODE_REMOTE_URL"
  fi
  REMOTE_TAG_LINES="$(git -C "$REPO_ROOT" ls-remote --tags gitcode "refs/tags/$TAG" "refs/tags/$TAG^{}" 2>/dev/null || true)"
  GITCODE_SHA="$(printf '%s\n' "$REMOTE_TAG_LINES" | awk -v ref="refs/tags/$TAG^{}" '$2 == ref {print $1}')"
  if [ -z "$GITCODE_SHA" ]; then
    GITCODE_SHA="$(printf '%s\n' "$REMOTE_TAG_LINES" | awk -v ref="refs/tags/$TAG" '$2 == ref {print $1}')"
  fi
  if [ -z "$GITCODE_SHA" ]; then
    info "GitCode 缺少标签 ${TAG}，推送中 ..."
    git -C "$REPO_ROOT" push gitcode "refs/tags/$TAG:refs/tags/$TAG"
  elif [ "$GITCODE_SHA" != "$LOCAL_SHA" ]; then
    die "GitCode 标签 ${TAG} 指向 ${GITCODE_SHA}，与源提交 ${LOCAL_SHA} 不一致；不要强制覆盖，先核对两侧历史"
  else
    info "GitCode 标签已指向同一提交"
  fi
else
  info "已跳过 GitCode tag 检查（--skip-tag-push）"
fi

# 4. 从 GitHub Release 下载附件
RELEASE_JSON="$BUNDLE_DIR/.github-release.json"
if ! gh release view "$TAG" -R "$GITHUB_REPO" --json tagName,isDraft,body > "$RELEASE_JSON"; then
  die "无法读取 GitHub Release $TAG"
fi
node -e '
const fs = require("node:fs");
const [source, notes, tag] = process.argv.slice(1);
const release = JSON.parse(fs.readFileSync(source, "utf8"));
if (release.tagName !== tag) throw new Error(`Release tag ${release.tagName} 与 ${tag} 不一致`);
if (release.isDraft) throw new Error("Release 仍是草稿，不能同步");
const body = typeof release.body === "string" ? release.body.trim() : "";
if (!body) throw new Error("GitHub Release 没有发布说明");
fs.writeFileSync(notes, release.body, "utf8");
' "$RELEASE_JSON" "$NOTES_PATH" "$TAG" || die "GitHub Release $TAG 的元数据不可用"
rm -f "$RELEASE_JSON"

for asset in "$STUDENT_NAME" "$TEACHER_NAME" "$CHECKSUMS_NAME"; do
  if [ -s "$BUNDLE_DIR/$asset" ]; then
    info "已存在 ${asset}，跳过下载"
    continue
  fi
  info "从 GitHub 下载 $asset ..."
  gh release download "$TAG" -R "$GITHUB_REPO" --pattern "$asset" --dir "$BUNDLE_DIR" --clobber \
    || die "下载 $asset 失败"
done

# 5. 本机校验摘要
verify_bundle

if [ "$DRY_RUN" -eq 1 ]; then
  info "试运行完成：附件已下载并校验，未读取 Token、未上传。"
  info "目录：$BUNDLE_DIR"
  exit 0
fi

# 6. Token（只在本进程内有效）
TOKEN_FROM_PROMPT=0
if [ -z "${GITCODE_TOKEN:-}" ]; then
  [ -t 0 ] || die "GITCODE_TOKEN 未设置，且当前不是交互终端"
  printf 'GitCode Token（输入不回显）: ' >&2
  stty -echo || true
  if ! IFS= read -r GITCODE_TOKEN; then
    stty echo || true
    die "未读取到 GitCode Token"
  fi
  stty echo || true
  printf '\n' >&2
  export GITCODE_TOKEN
  TOKEN_FROM_PROMPT=1
fi

cleanup() {
  if [ "$TOKEN_FROM_PROMPT" -eq 1 ]; then
    unset GITCODE_TOKEN
  fi
}
trap cleanup EXIT

export GITCODE_OWNER="${GITCODE_OWNER:-$DEFAULT_GITCODE_OWNER}"
export GITCODE_REPO="${GITCODE_REPO:-$DEFAULT_GITCODE_REPO}"

# 7. 上传并读回校验
info "上传到 GitCode（${GITCODE_OWNER}/${GITCODE_REPO}）..."
set +e
node "$SCRIPT_DIR/publish-gitcode-release.cjs" \
  "$TAG" "$VERSION" "$LOCAL_SHA" \
  "$NOTES_PATH" \
  "$STUDENT_PATH" \
  "$TEACHER_PATH" \
  "$CHECKSUMS_PATH" | tee "$RESULT_PATH"
UPLOAD_STATUS="${PIPESTATUS[0]}"
set -e

if [ "$UPLOAD_STATUS" -ne 0 ]; then
  die "GitCode 上传失败（退出码 ${UPLOAD_STATUS}）；本地附件已保留，可直接重跑"
fi

PAGE_URL="$(sed -n 's/^[[:space:]]*"releasePageUrl":[[:space:]]*"\([^"]*\)".*/\1/p' "$RESULT_PATH" | head -1)"
info "完成。GitCode Release：${PAGE_URL:-https://gitcode.com/$GITCODE_OWNER/$GITCODE_REPO/releases/tag/$TAG}"
info "结果记录：$RESULT_PATH"
