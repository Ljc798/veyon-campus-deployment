#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/VeyonCampus.App/VeyonCampus.App.csproj"
dotnet_cmd="${VEYONCAMPUS_DOTNET:-dotnet}"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  printf '此脚本仅为 Apple Silicon macOS 构建教师端。\n' >&2
  exit 2
fi

if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  printf '找不到 .NET SDK。请安装 .NET 10，或设置 VEYONCAMPUS_DOTNET。\n' >&2
  exit 2
fi

version="$("$dotnet_cmd" msbuild "$project" -getProperty:Version -p:VeyonCampusRole=TeacherConsole)"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]]; then
  printf '无法从项目读取有效版本号：%s\n' "$version" >&2
  exit 2
fi

artifact_dir="$repo_root/artifacts/macos"
mkdir -p "$artifact_dir"
stage_dir="$(mktemp -d "$artifact_dir/.teacher-$version.XXXXXX")"
staged_bundle="$stage_dir/Veyon Campus Teacher.app"
publish_dir="$stage_dir/publish"
final_bundle="$artifact_dir/Veyon Campus Teacher-$version.app"
trap 'rm -rf "$stage_dir"' EXIT

"$dotnet_cmd" restore "$project" --runtime osx-arm64 --locked-mode \
  -p:VeyonCampusRole=TeacherConsole
"$dotnet_cmd" publish "$project" --configuration Release --runtime osx-arm64 \
  --self-contained true --no-restore --output "$publish_dir" \
  -p:VeyonCampusRole=TeacherConsole -p:UseAppHost=true

if [[ ! -x "$publish_dir/VeyonCampus.Teacher" ]]; then
  printf '发布目录缺少可执行的 VeyonCampus.Teacher。\n' >&2
  exit 1
fi
if [[ -e "$publish_dir/Worker" || -e "$publish_dir/StudentSetup.exe" ]]; then
  printf 'Mac 教师端发布目录含有 Windows 或学生部署载荷，已停止打包。\n' >&2
  exit 1
fi
if ! grep -q '"role":"TeacherConsole"' "$publish_dir/veyon-campus-role.json"; then
  printf 'Mac 教师端角色标记无效，已停止打包。\n' >&2
  exit 1
fi

mkdir -p "$staged_bundle/Contents/MacOS" "$staged_bundle/Contents/Resources"
ditto "$publish_dir" "$staged_bundle/Contents/MacOS"
cp "$repo_root/src/VeyonCampus.App/Assets/veyon-campus-mark.png" \
  "$staged_bundle/Contents/Resources/veyon-campus-mark.png"
chmod +x "$staged_bundle/Contents/MacOS/VeyonCampus.Teacher"

cat >"$staged_bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleDisplayName</key><string>Veyon Campus 教师端</string>
  <key>CFBundleExecutable</key><string>VeyonCampus.Teacher</string>
  <key>CFBundleIdentifier</key><string>com.veyon.campus.teacher</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Veyon Campus Teacher</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>教师端需要连接校园网中的学生电脑并向配对手机提供课堂控制服务。</string>
  <key>NSAppTransportSecurity</key>
  <dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict>
</plist>
PLIST

plutil -lint "$staged_bundle/Contents/Info.plist" >/dev/null

if [[ -e "$final_bundle" ]]; then
  backup_bundle="$final_bundle.previous.$(date +%Y%m%d%H%M%S)"
  mv "$final_bundle" "$backup_bundle"
  if ! mv "$staged_bundle" "$final_bundle"; then
    mv "$backup_bundle" "$final_bundle"
    exit 1
  fi
  printf '已保留上一版应用：%s\n' "$backup_bundle"
else
  mv "$staged_bundle" "$final_bundle"
fi

printf 'Mac 教师端已生成：%s\n' "$final_bundle"
printf '这是本地未签名构建；分发给其他 Mac 前还需 Developer ID 签名和公证。\n'
