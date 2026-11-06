#!/usr/bin/env bash
# 生成 Debian / Ubuntu 安装包
set -euo pipefail

PAYLOAD="${1:?用法: build-deb.sh <发布目录> [输出目录]}"
OUT="${2:-$(dirname "$0")/../dist}"
VERSION="1.0.2"
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

APP=/opt/pcl-l
mkdir -p "$ROOT$APP" "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps" "$ROOT/DEBIAN"
cp -a "$PAYLOAD"/. "$ROOT$APP/"
chmod 755 "$ROOT$APP/PCL.Frontend.Avalonia"

cp "$(dirname "$0")/pcl-l.desktop" "$ROOT/usr/share/applications/pcl-l.desktop"
cp "$(dirname "$0")/arch/pcl-l.png" "$ROOT/usr/share/icons/hicolor/256x256/apps/pcl-l.png"

cat > "$ROOT/DEBIAN/control" <<CTRL
Package: pcl-l
Version: $VERSION
Section: games
Priority: optional
Architecture: amd64
Maintainer: PCL-L <pcl-l@example.com>
Installed-Size: $(du -sk "$ROOT$APP" | cut -f1)
Depends: libc6 (>= 2.34), libicu | libicu72 | libicu74, libssl3 | libssl3t64, zlib1g, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libxrandr2, libxi6, libgl1, libglib2.0-0
Description: PCL-L
 Plain Craft Launcher for Linux, with instance management, mod
 downloads, Java runtime handling and P2P multiplayer.
CTRL

mkdir -p "$OUT"
dpkg-deb --build --root-owner-group "$ROOT" "$OUT/PCL-L-${VERSION}-amd64.deb"
echo "已生成: $OUT/PCL-L-${VERSION}-amd64.deb"
