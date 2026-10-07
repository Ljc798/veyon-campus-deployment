#!/bin/bash

repo_root="$(cd "$(dirname "$0")" && pwd)"
cd "$repo_root" || exit 2

printf 'Veyon Campus CloudBase 合成配置包验收\n'
printf '目标：已备份的共享体验环境。接下来需要本机 CloudBase 管理员登录。\n\n'

bash "$repo_root/scripts/run-live-package-api-e2e.sh" --confirm-live-synthetic-test
status=$?

printf '\n验收脚本退出码：%s\n' "$status"
read -r -p '按回车关闭此窗口。' _
exit "$status"
