#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
image_tag="${VEYON_HELPER_IMAGE:-veyon-campus-helper:4.11.4}"
container_name="${VEYON_HELPER_CONTAINER:-veyon-campus-helper-arm64}"

if docker container inspect "${container_name}" >/dev/null 2>&1; then
  image_in_use="$(docker inspect --format '{{.Config.Image}}' "${container_name}")"
  if [[ "${image_in_use}" != "${image_tag}" ]]; then
    echo "Container ${container_name} uses ${image_in_use}; expected ${image_tag}. Refusing to reuse it." >&2
    exit 1
  fi
  tmpfs_mounts="$(docker inspect --format '{{json .HostConfig.Tmpfs}}' "${container_name}")"
  if [[ "${tmpfs_mounts}" != *'"/run/veyon-helper"'* ]]; then
    echo "Container ${container_name} does not mount /run/veyon-helper as tmpfs; refusing to load keys." >&2
    exit 1
  fi
  docker start "${container_name}" >/dev/null
else
  docker run --detach \
    --platform linux/arm64 \
    --name "${container_name}" \
    --restart unless-stopped \
    --network bridge \
    --tmpfs /run/veyon-helper:rw,nosuid,nodev,noexec,size=96m \
    --entrypoint /bin/sh \
    "${image_tag}" \
    -c 'mkdir -p /run/veyon-helper/screenshots && chmod 700 /run/veyon-helper /run/veyon-helper/screenshots && /usr/local/bin/veyon-cli config set Directories/Screenshots /run/veyon-helper/screenshots >/dev/null && /usr/local/bin/veyon-cli config set Authentication/PrivateKeyBaseDir /run/veyon-helper/keys/private >/dev/null && /usr/local/bin/veyon-cli config set Authentication/PublicKeyBaseDir /run/veyon-helper/keys/public >/dev/null && exec sleep infinity' \
    >/dev/null
fi

docker exec "${container_name}" mkdir -p /usr/local/lib/veyon-helper
docker cp "${script_dir}/capture.py" "${container_name}:/usr/local/lib/veyon-helper/capture.py" >/dev/null
docker exec "${container_name}" chmod 0555 /usr/local/lib/veyon-helper/capture.py
docker exec "${container_name}" mkdir -p /run/veyon-helper/screenshots /run/veyon-helper/keys/private /run/veyon-helper/keys/public
docker exec "${container_name}" chmod 0700 /run/veyon-helper /run/veyon-helper/screenshots \
  /run/veyon-helper/keys /run/veyon-helper/keys/private /run/veyon-helper/keys/public
docker exec "${container_name}" /usr/local/bin/veyon-cli config set \
  Directories/Screenshots /run/veyon-helper/screenshots >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli config set \
  Authentication/PrivateKeyBaseDir /run/veyon-helper/keys/private >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli config set \
  Authentication/PublicKeyBaseDir /run/veyon-helper/keys/public >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli --version
