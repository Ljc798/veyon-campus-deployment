#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
image_tag="${VEYON_HELPER_IMAGE:-veyon-campus-helper:4.11.4}"

docker build \
  --platform linux/arm64 \
  --build-arg VEYON_VERSION=4.11.4 \
  --build-arg VEYON_SOURCE_SHA256=92701c1ca8f794a738c6d8ecec4b2a85bb63e2906e369df9c2d0ee97a1f1ce8 \
  --tag "${image_tag}" \
  --file "${script_dir}/Dockerfile" \
  "${script_dir}"

docker run --rm --platform linux/arm64 --entrypoint /usr/local/bin/veyon-cli \
  "${image_tag}" --version
