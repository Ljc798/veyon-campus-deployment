#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
image_tag="${VEYON_HELPER_IMAGE:-veyon-campus-helper:4.11.4}"

resolve_ipv4_list() {
  local host="$1"
  local address=""
  local response
  response="$(curl --fail --silent --show-error --connect-timeout 5 --max-time 15 \
    --resolve cloudflare-dns.com:443:1.1.1.1 \
    "https://cloudflare-dns.com/dns-query?name=${host}&type=A" \
    --header 'accept: application/dns-json')" || return 1
  address="$(printf '%s' "$response" | grep -o '"type":1[^}]*' |
    sed -n 's/.*"data":"\([0-9.]*\)".*/\1/p')"

  address="$(printf '%s\n' "$address" | awk '/^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/ && !seen[$0]++')"
  [[ -n "$address" ]] || return 1
  printf '%s\n' "$address"
}

build_args=(--platform linux/arm64)
for host in ports.ubuntu.com github.com release-assets.githubusercontent.com; do
  if ! addresses="$(resolve_ipv4_list "$host")"; then
    printf '无法解析 helper 构建所需的主机：%s。请检查网络 DNS 后重试。\n' "$host" >&2
    exit 1
  fi
  while IFS= read -r address; do
    [[ -n "$address" ]] && build_args+=(--add-host "${host}:${address}")
  done <<< "$addresses"
done

docker build \
  "${build_args[@]}" \
  --build-arg VEYON_VERSION=4.11.4 \
  --build-arg VEYON_SOURCE_SHA256=92701c1ca8f794a738c6cd8ecec4b2a85bb63e2906e369df9c2d0ee97a1f1ce8 \
  --tag "${image_tag}" \
  --file "${script_dir}/Dockerfile" \
  "${script_dir}"

docker run --rm --platform linux/arm64 --entrypoint /usr/local/bin/veyon-cli \
  "${image_tag}" --version
