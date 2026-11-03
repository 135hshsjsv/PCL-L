#!/usr/bin/env bash
# 生成 Debian / Ubuntu 安装包
set -euo pipefail

PAYLOAD="${1:?用法: build-deb.sh <发布目录> [输出目录]}"
OUT="${2:-$(dirname "$0")/../dist}"
VERSION="1.0.0"
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

APP=/opt/plaincraftlauncher
mkdir -p "$ROOT$APP" "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/DEBIAN"
cp -a "$PAYLOAD"/. "$ROOT$APP/"
chmod 755 "$ROOT$APP/PCL.Frontend.Avalonia"

cp "$(dirname "$0")/plaincraftlauncher.desktop" "$ROOT/usr/share/applications/plaincraftlauncher.desktop"
cp "$(dirname "$0")/../PCL.Frontend.Avalonia/Assets/icon.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/plaincraftlauncher.png"

cat > "$ROOT/DEBIAN/control" <<CTRL
Package: plaincraftlauncher
Version: $VERSION
Section: games
Priority: optional
Architecture: amd64
Maintainer: PlainCraftLauncher - Linux <pcl-l@example.com>
Installed-Size: $(du -sk "$ROOT$APP" | cut -f1)
Depends: libc6 (>= 2.34), libicu | libicu72 | libicu74, libssl3 | libssl3t64, zlib1g, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libxrandr2, libxi6, libgl1, libglib2.0-0
Description: PlainCraftLauncher - Linux
 A cross-platform Plain Craft Launcher build for Linux, with instance
 management, mod downloads, Java runtime handling and P2P multiplayer.
CTRL

mkdir -p "$OUT"
dpkg-deb --build --root-owner-group "$ROOT" "$OUT/PlainCraftLauncher-Linux-${VERSION}-amd64.deb"
echo "已生成: $OUT/PlainCraftLauncher-Linux-${VERSION}-amd64.deb"
