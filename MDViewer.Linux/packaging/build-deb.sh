#!/usr/bin/env bash
# Builds MDViewer.Linux/bin/mdviewer_<version>_<arch>.deb. Run on Linux or in WSL:
#
#   MDViewer.Linux/packaging/build-deb.sh [amd64|arm64]
#
# Publishes with `dotnet` from PATH. From WSL without a Linux SDK, point DOTNET at
# the Windows one — it cross-publishes for Linux without complaint:
#
#   DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe" MDViewer.Linux/packaging/build-deb.sh
#
# The app is published self-contained, so the machine needs no .NET runtime; the
# package depends only on the native libraries Photino loads.
set -euo pipefail

arch=${1:-amd64}
case $arch in
    amd64) rid=linux-x64 ;;
    arm64) rid=linux-arm64 ;;
    *) echo "usage: $0 [amd64|arm64]" >&2; exit 2 ;;
esac

repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"
dotnet=${DOTNET:-dotnet}

# One version for both platforms: the Windows project's ApplicationDisplayVersion,
# which is already the number bumped for every release.
version=$(sed -n 's:.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*:\1:p' MDViewer/MDViewer.csproj | head -n 1)
[ -n "$version" ] || { echo "no ApplicationDisplayVersion in MDViewer/MDViewer.csproj" >&2; exit 1; }

# Relative paths throughout: they mean the same thing to a Windows dotnet.exe.
publish=MDViewer.Linux/bin/publish-$rid
rm -rf "$publish"
"$dotnet" publish MDViewer.Linux -c Release -r "$rid" --self-contained -p:Version="$version" -o "$publish"

# Staged on the Linux filesystem, not beside the repo: on a /mnt/c checkout every
# file reads as mode 777, and the package needs real permissions.
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
root=$stage/root
lib=$root/usr/lib/mdviewer
share=$root/usr/share

mkdir -p "$lib" "$root/usr/bin" "$root/DEBIAN" "$share/applications" "$share/doc/mdviewer" \
    "$share/icons/hicolor/scalable/apps" "$share/icons/hicolor/256x256/apps"

cp -R "$publish"/. "$lib"/
rm -f "$lib"/*.pdb

# The apphost resolves its own symlink, so the app still finds its files (and
# wwwroot) in /usr/lib/mdviewer when started as /usr/bin/mdviewer.
ln -s ../lib/mdviewer/mdviewer "$root/usr/bin/mdviewer"

cp MDViewer.Linux/packaging/nz.pontifex.mdviewer.desktop "$share/applications/"
cp MDViewer.Linux/mdviewer.svg "$share/icons/hicolor/scalable/apps/nz.pontifex.mdviewer.svg"
cp MDViewer.Linux/mdviewer.png "$share/icons/hicolor/256x256/apps/nz.pontifex.mdviewer.png"
cp LICENSE "$share/doc/mdviewer/copyright"

find "$root" -type d -exec chmod 755 {} +
find "$root" -type f -exec chmod 644 {} +
chmod 755 "$lib/mdviewer"
[ -f "$lib/createdump" ] && chmod 755 "$lib/createdump"

# No ICU or GTK in Depends: WebKitGTK already depends on both, and naming ICU
# here would mean naming its soname-versioned package for every release.
# No maintainer scripts either: the desktop database and icon cache are
# refreshed by dpkg triggers from desktop-file-utils and the icon theme.
cat > "$root/DEBIAN/control" <<EOF
Package: mdviewer
Version: $version
Architecture: $arch
Maintainer: Pontifex <ron@pontifex.nz>
Installed-Size: $(du -sk "$root/usr" | cut -f1)
Depends: libwebkit2gtk-4.1-0, libnotify4
Section: text
Priority: optional
Homepage: https://github.com/pontifexrm/MDViewer
Description: Markdown viewer and editor, with PDF, Word and ePub export
 Opens .md files rendered, with a split-pane editor and live preview, and
 reads .epub books. Prints, and exports to PDF, Word and ePub. Pasting
 formatted text from Word, a browser or an email converts it to markdown.
EOF

out=MDViewer.Linux/bin/mdviewer_${version}_${arch}.deb
dpkg-deb --build --root-owner-group -Zxz "$root" "$out"
echo "built $out"
