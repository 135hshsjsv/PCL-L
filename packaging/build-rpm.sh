#!/usr/bin/env bash
# 生成 Fedora / RHEL / openSUSE 安装包
set -euo pipefail

PAYLOAD="${1:?用法: build-rpm.sh <发布目录> [输出目录]}"
OUT="${2:-$(dirname "$0")/../dist}"
VERSION="1.0.2"
SPEC_DIR="$(cd "$(dirname "$0")" && pwd)"
TOPDIR="$(mktemp -d)"
trap 'rm -rf "$TOPDIR"' EXIT

mkdir -p "$TOPDIR"/{BUILD,RPMS,SOURCES,SPECS,BUILDROOT}

cat > "$TOPDIR/SPECS/pcl-l.spec" <<SPEC
Name:           pcl-l
Version:        $VERSION
Release:        1
Summary:        PCL-L (Plain Craft Launcher for Linux)
License:        Apache-2.0
BuildArch:      x86_64
Requires:       glibc, icu, openssl-libs, zlib, fontconfig, freetype, libX11, libICE, libSM, libXrandr, libXi, mesa-libGL, glib2
%description
Plain Craft Launcher for Linux, with instance management, mod downloads,
Java runtime handling and P2P multiplayer.
%install
mkdir -p %{buildroot}/opt/pcl-l
cp -a $PAYLOAD/. %{buildroot}/opt/pcl-l/
chmod 755 %{buildroot}/opt/pcl-l/PCL.Frontend.Avalonia
install -Dpm 644 $SPEC_DIR/pcl-l.desktop %{buildroot}/usr/share/applications/pcl-l.desktop
install -Dpm 644 $SPEC_DIR/arch/pcl-l.png %{buildroot}/usr/share/icons/hicolor/256x256/apps/pcl-l.png
%files
/opt/pcl-l
/usr/share/applications/pcl-l.desktop
/usr/share/icons/hicolor/256x256/apps/pcl-l.png
%changelog
* $(date '+%a %b %d %Y') PCL-L <pcl-l@example.com> - $VERSION-1
- Initial Linux package.
SPEC

rpmbuild -bb --define "_topdir $TOPDIR" "$TOPDIR/SPECS/pcl-l.spec" >/dev/null
mkdir -p "$OUT"
cp "$TOPDIR"/RPMS/x86_64/*.rpm "$OUT/PCL-L-${VERSION}-1.x86_64.rpm"
echo "已生成: $OUT/PCL-L-${VERSION}-1.x86_64.rpm"
