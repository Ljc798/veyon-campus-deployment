#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -eq 0 ]]; then
  printf 'Usage: bash scripts/with-domestic-dns.sh <command> [arguments...]\n' >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
resolver_loader="$script_dir/cloudbase-domestic-dns.cjs"
if [[ ! -f "$resolver_loader" ]]; then
  printf 'Domestic DNS resolver helper is missing: %s\n' "$resolver_loader" >&2
  exit 2
fi
if ! command -v node >/dev/null 2>&1; then
  printf 'Node.js is required to run the domestic DNS helper.\n' >&2
  exit 127
fi

export NODE_OPTIONS="${NODE_OPTIONS:+$NODE_OPTIONS }--require=$resolver_loader"
"$@"
