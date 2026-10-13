#!/usr/bin/env bash
set -euo pipefail

owner="${GITCODE_OWNER:-}"
repo="${GITCODE_REPO:-}"
token="${GITCODE_TOKEN:-}"
source_commit="${SOURCE_COMMIT:-${GITHUB_SHA:-}}"

if [[ ! "$owner" =~ ^[A-Za-z0-9_.-]+$ || ! "$repo" =~ ^[A-Za-z0-9_.-]+$ ]]; then
  echo 'GITCODE_OWNER and GITCODE_REPO must be simple repository path segments.' >&2
  exit 1
fi
if [[ -z "$token" ]]; then
  echo 'GITCODE_TOKEN is required to mirror source to GitCode.' >&2
  exit 1
fi
if [[ ! "$source_commit" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo 'SOURCE_COMMIT must be a full Git commit SHA.' >&2
  exit 1
fi
git cat-file -e "${source_commit}^{commit}"

remote="https://gitcode.com/${owner}/${repo}.git"
askpass="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/gitcode-askpass.XXXXXX")"
cat > "$askpass" <<'ASKPASS'
#!/usr/bin/env sh
case "${1:-}" in
  *sername*) printf '%s\n' "$GITCODE_OWNER" ;;
  *) printf '%s\n' "$GITCODE_TOKEN" ;;
esac
ASKPASS
chmod 700 "$askpass"
trap 'rm -f "$askpass"' EXIT
export GIT_ASKPASS="$askpass"
export GIT_TERMINAL_PROMPT=0

remote_commit="$(git -c credential.helper= ls-remote "$remote" refs/heads/develop | awk 'NR == 1 { print $1 }')"
if [[ -z "$remote_commit" ]]; then
  echo 'GitCode develop is empty; creating it from the selected source commit.'
  git -c credential.helper= push "$remote" "${source_commit}:refs/heads/develop"
  exit 0
fi
if [[ ! "$remote_commit" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo 'GitCode returned an invalid develop commit SHA.' >&2
  exit 1
fi

git -c credential.helper= fetch --no-tags "$remote" \
  +refs/heads/develop:refs/remotes/gitcode/develop
if [[ "$remote_commit" == "$source_commit" ]]; then
  echo "GitCode develop already points to ${source_commit}."
elif git merge-base --is-ancestor refs/remotes/gitcode/develop "$source_commit"; then
  echo "Fast-forwarding GitCode develop to ${source_commit}."
  git -c credential.helper= push "$remote" "${source_commit}:refs/heads/develop"
elif git merge-base --is-ancestor "$source_commit" refs/remotes/gitcode/develop; then
  echo "GitCode develop already contains ${source_commit}; leaving its newer head in place."
else
  echo 'GitCode develop has commits outside this GitHub history; refusing to overwrite or force-push.' >&2
  exit 1
fi
