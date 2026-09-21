#!/usr/bin/env bash
# 取回 godot-xterm 插件（含三端预编译的 GDExtension 二进制）。
#
# 和镜像一样走脚本而不是入库：release 压缩包 10MB 全是各平台二进制，
# 进 git 没有意义。版本在这里钉死，换版本改这一行。
set -euo pipefail

VERSION="${GODOT_XTERM_VERSION:-v4.0.3}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
URL="https://github.com/lihop/godot-xterm/releases/download/$VERSION/godot-xterm-$VERSION.zip"
ZIP="$HERE/.addons-cache/godot-xterm-$VERSION.zip"

log() { printf '\033[36m[godot]\033[0m %s\n' "$*" >&2; }

mkdir -p "$(dirname "$ZIP")"
if [[ ! -f "$ZIP" ]]; then
  log "下载 godot-xterm $VERSION"
  curl -fSL --progress-bar -o "$ZIP" "$URL"
fi

log "解包到 addons/"
rm -rf "$HERE/addons/godot_xterm"
mkdir -p "$HERE/addons"
unzip -q "$ZIP" -d "$HERE/.addons-cache/extract"
# 压缩包里可能带一层顶层目录，统一收敛
SRC="$(find "$HERE/.addons-cache/extract" -maxdepth 3 -type d -name godot_xterm | head -1)"
[[ -n "$SRC" ]] || { echo "压缩包里找不到 godot_xterm 目录" >&2; exit 1; }
cp -r "$SRC" "$HERE/addons/"
rm -rf "$HERE/.addons-cache/extract"

log "已装: $(ls "$HERE/addons/godot_xterm/lib" 2>/dev/null | wc -l) 个平台二进制"
grep -m1 compatibility_minimum "$HERE/addons/godot_xterm/"*.gdextension | sed 's/^/  /'
