#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/VeyonCampus.App/VeyonCampus.App.csproj"
dotnet_cmd="${VEYONCAMPUS_DOTNET:-dotnet}"
signing_identity="${VEYONCAMPUS_MACOS_SIGNING_IDENTITY:-}"
notary_profile="${VEYONCAMPUS_MACOS_NOTARY_PROFILE:-}"
classroom_quick_control_pilot="${VEYONCAMPUS_CLASSROOM_QUICK_CONTROL_PILOT:-false}"

if [[ "$classroom_quick_control_pilot" != "true" && "$classroom_quick_control_pilot" != "false" ]]; then
  printf 'VEYONCAMPUS_CLASSROOM_QUICK_CONTROL_PILOT 必须是 true 或 false。\n' >&2
  exit 2
fi

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  printf '此脚本仅为 Apple Silicon macOS 构建教师端。\n' >&2
  exit 2
fi

if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  printf '找不到 .NET SDK。请安装 .NET 10，或设置 VEYONCAMPUS_DOTNET。\n' >&2
  exit 2
fi

if [[ -n "$notary_profile" && -z "$signing_identity" ]]; then
  printf '设置公证配置前，必须先设置 Developer ID Application 签名身份。\n' >&2
  exit 2
fi
if [[ -n "$signing_identity" ]]; then
  case "$signing_identity" in
    "Developer ID Application:"*) ;;
    *)
      printf '签名身份必须是 Developer ID Application 证书。\n' >&2
      exit 2
      ;;
  esac
  identity_list="$(security find-identity -v -p codesigning)"
  if ! grep -Fq "\"$signing_identity\"" <<<"$identity_list"; then
    printf '钥匙串中找不到签名身份：%s\n' "$signing_identity" >&2
    exit 2
  fi
fi
if [[ -n "$notary_profile" ]] && ! command -v xcrun >/dev/null 2>&1; then
  printf '找不到 xcrun；无法提交 Apple 公证。\n' >&2
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
bundle_display_name="Veyon Campus 教师端"
if [[ "$classroom_quick_control_pilot" == "true" ]]; then
  final_bundle="$artifact_dir/Veyon Campus Teacher-$version-Pilot.app"
  bundle_display_name="Veyon Campus 教师端（课堂管控试点）"
fi
trap 'rm -rf "$stage_dir"' EXIT

"$dotnet_cmd" restore "$project" --locked-mode \
  -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusTargetRuntime=osx-arm64 \
  -p:VeyonCampusClassroomQuickControlPilot="$classroom_quick_control_pilot"
"$dotnet_cmd" publish "$project" --configuration Release --runtime osx-arm64 \
  --self-contained true --no-restore --output "$publish_dir" \
  -p:VeyonCampusRole=TeacherConsole -p:VeyonCampusTargetRuntime=osx-arm64 \
  -p:VeyonCampusClassroomQuickControlPilot="$classroom_quick_control_pilot" -p:UseAppHost=true

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
ditto "$repo_root/scripts/veyon-helper" "$staged_bundle/Contents/Resources/veyon-helper"
chmod 0555 "$staged_bundle/Contents/Resources/veyon-helper/build.sh" \
  "$staged_bundle/Contents/Resources/veyon-helper/start.sh"
chmod +x "$staged_bundle/Contents/MacOS/VeyonCampus.Teacher"

cat >"$staged_bundle/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
  <key>CFBundleDisplayName</key><string>$bundle_display_name</string>
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

signing_status="未签名"
if [[ -n "$signing_identity" ]]; then
  entitlements_file="$repo_root/scripts/macos-teacher.entitlements"
  if [[ ! -f "$entitlements_file" ]]; then
    printf '缺少 .NET Mac 教师端签名权限文件。\n' >&2
    exit 1
  fi

  while IFS= read -r -d '' code_file; do
    file_description="$(/usr/bin/file -b "$code_file")"
    [[ "$file_description" == *Mach-O* ]] || continue

    codesign_arguments=(--force --options runtime --timestamp --sign "$signing_identity")
    if [[ "$code_file" == "$staged_bundle/Contents/MacOS/VeyonCampus.Teacher" ]]; then
      codesign_arguments+=(--entitlements "$entitlements_file")
    fi
    codesign "${codesign_arguments[@]}" "$code_file"
  done < <(find "$staged_bundle/Contents/MacOS" -type f -print0)

  codesign --force --options runtime --timestamp --sign "$signing_identity" "$staged_bundle"
  codesign --verify --deep --strict --verbose=2 "$staged_bundle"
  signing_status="Developer ID 已签名"

  if [[ -n "$notary_profile" ]]; then
    notary_archive="$stage_dir/Veyon-Campus-Teacher-notarization.zip"
    ditto -c -k --sequesterRsrc --keepParent "$staged_bundle" "$notary_archive"
    notary_result="$(xcrun notarytool submit "$notary_archive" \
      --keychain-profile "$notary_profile" --wait --output-format json)"
    if ! grep -Eq '"status"[[:space:]]*:[[:space:]]*"Accepted"' <<<"$notary_result"; then
      printf 'Apple 公证未通过：\n%s\n' "$notary_result" >&2
      exit 1
    fi
    xcrun stapler staple "$staged_bundle"
    xcrun stapler validate "$staged_bundle"
    spctl --assess --type execute --verbose=2 "$staged_bundle"
    signing_status="Developer ID 已签名并完成公证"
  fi
fi

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
printf '签名状态：%s\n' "$signing_status"
if [[ "$classroom_quick_control_pilot" == "true" ]]; then
  printf '课堂快捷管控试点：已启用（独立本机预览包）。\n'
fi
if [[ -n "$signing_identity" && -z "$notary_profile" ]]; then
  printf '此应用已签名但未公证；跨设备分发前仍需设置 VEYONCAMPUS_MACOS_NOTARY_PROFILE 完成公证。\n'
elif [[ -z "$signing_identity" ]]; then
  printf '本机没有配置 Developer ID 身份；此构建仅用于本机预览。\n'
fi
