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
  privileged="$(docker inspect --format '{{.HostConfig.Privileged}}' "${container_name}")"
  network_mode="$(docker inspect --format '{{.HostConfig.NetworkMode}}' "${container_name}")"
  publish_all_ports="$(docker inspect --format '{{.HostConfig.PublishAllPorts}}' "${container_name}")"
  port_bindings="$(docker inspect --format '{{json .HostConfig.PortBindings}}' "${container_name}")"
  tmpfs_options="$(docker inspect --format '{{index .HostConfig.Tmpfs "/run/veyon-helper"}}' "${container_name}")"
  host_binds="$(docker inspect --format '{{json .HostConfig.Binds}}' "${container_name}")"
  host_mounts="$(docker inspect --format '{{json .HostConfig.Mounts}}' "${container_name}")"
  volumes_from="$(docker inspect --format '{{json .HostConfig.VolumesFrom}}' "${container_name}")"
  mounted_volumes="$(docker inspect --format '{{json .Mounts}}' "${container_name}")"
  if [[ "${privileged}" != "false" ]]; then
    echo "Container ${container_name} is privileged; refusing to reuse it." >&2
    exit 1
  fi
  if [[ "${network_mode}" != "bridge" || "${publish_all_ports}" != "false" ||
        ( "${port_bindings}" != "null" && "${port_bindings}" != "{}" ) ]]; then
    echo "Container ${container_name} has an unexpected network or published ports; refusing to reuse it." >&2
    exit 1
  fi
  if [[ "${tmpfs_options}" != "rw,nosuid,nodev,noexec,size=96m" ]]; then
    echo "Container ${container_name} does not use the expected private 96 MiB tmpfs; refusing to load keys." >&2
    exit 1
  fi
  for mount_config in "${host_binds}" "${host_mounts}" "${volumes_from}" "${mounted_volumes}"; do
    if [[ "${mount_config}" != "null" && "${mount_config}" != "[]" ]]; then
      echo "Container ${container_name} has a host bind or volume mount; refusing to load keys." >&2
      exit 1
    fi
  done
  is_running="$(docker inspect --format '{{.State.Running}}' "${container_name}")"
  if [[ "${is_running}" != "true" ]]; then
    docker start "${container_name}" >/dev/null
  fi
else
  docker run --detach \
    --platform linux/arm64 \
    --name "${container_name}" \
    --restart unless-stopped \
    --network bridge \
    --tmpfs /run/veyon-helper:rw,nosuid,nodev,noexec,size=96m \
    --entrypoint /bin/sh \
    "${image_tag}" \
    -c 'mkdir -p /run/veyon-helper/screenshots && chmod 700 /run/veyon-helper /run/veyon-helper/screenshots && /usr/local/bin/veyon-cli config set Authentication/Method 1 >/dev/null && /usr/local/bin/veyon-cli config set Directories/Screenshots /run/veyon-helper/screenshots >/dev/null && /usr/local/bin/veyon-cli config set Authentication/PrivateKeyBaseDir /run/veyon-helper/keys/private >/dev/null && /usr/local/bin/veyon-cli config set Authentication/PublicKeyBaseDir /run/veyon-helper/keys/public >/dev/null && exec sleep infinity' \
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
  Authentication/Method 1 >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli config set \
  Authentication/PrivateKeyBaseDir /run/veyon-helper/keys/private >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli config set \
  Authentication/PublicKeyBaseDir /run/veyon-helper/keys/public >/dev/null
docker exec "${container_name}" /usr/local/bin/veyon-cli --version
