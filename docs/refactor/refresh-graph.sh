#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo_root"
if ! command -v graphify >/dev/null 2>&1; then
  echo "graphify is required on PATH" >&2
  exit 127
fi
graphify update . --force --no-cluster
python3 docs/refactor/extract-device-flow-graph.py
