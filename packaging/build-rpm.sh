#!/usr/bin/env bash
# 生成 Fedora / RHEL / openSUSE 安装包
set -euo pipefail

PAYLOAD="${1:?用法: build-rpm.sh <发布目录> [输出目录]}"
OUT="${2:-$(dirname "$0")/../dist}"
VERSION="1.0.0"
SPEC_DIR="$(cd "$(dirname "$0")" && pwd)"
TOPDIR="$(mktemp -d)"
trap 'rm -rf "$TOPDIR"' EXIT

mkdir -p "$TOPDIR"/{BUILD,RPMS,SOURCES,SPECS,BUILDROOT}

cat > "$TOPDIR/SPECS/plaincraftlauncher.spec" <<SPEC
Name:           plaincraftlauncher
Version:        $VERSION
Release:        1
Summary:        PlainCraftLauncher - Linux
License:        Apache-2.0
BuildArch:      x86_64
Requires:       glibc, icu, openssl-libs, zlib, fontconfig, freetype, libX11, libICE, libSM, libXrandr, libXi, mesa-libGL, glib2
%description
A cross-platform Plain Craft Launcher build for Linux, with instance
management, mod downloads, Java runtime handling and P2P multiplayer.
%install
mkdir -p %{buildroot}/opt/plaincraftlauncher
cp -a $PAYLOAD/. %{buildroot}/opt/plaincraftlauncher/
chmod 755 %{buildroot}/opt/plaincraftlauncher/PCL.Frontend.Avalonia
install -Dpm 644 $SPEC_DIR/plaincraftlauncher.desktop %{buildroot}/usr/share/applications/plaincraftlauncher.desktop
install -Dpm 644 $SPEC_DIR/../PCL.Frontend.Avalonia/Assets/icon.png %{buildroot}/usr/share/icons/hicolor/256x256/apps/plaincraftlauncher.png
%files
/opt/plaincraftlauncher
/usr/share/applications/plaincraftlauncher.desktop
/usr/share/icons/hicolor/256x256/apps/plaincraftlauncher.png
%changelog
* $(date '+%a %b %d %Y') PlainCraftLauncher - Linux <pcl-l@example.com> - $VERSION-1
- Initial Linux package.
SPEC

rpmbuild -bb --define "_topdir $TOPDIR" "$TOPDIR/SPECS/plaincraftlauncher.spec" >/dev/null
mkdir -p "$OUT"
cp "$TOPDIR"/RPMS/x86_64/*.rpm "$OUT/PlainCraftLauncher-Linux-${VERSION}-1.x86_64.rpm"
echo "已生成: $OUT/PlainCraftLauncher-Linux-${VERSION}-1.x86_64.rpm"
