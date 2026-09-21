#!/usr/bin/env bash
# 把 Windows 验证需要、但只能在 Linux 上产出的东西打成一个 zip。
#
# 客户机镜像靠 apk / cpio / depmod / mke2fs 构建，Windows 上没有这套工具链，
# 所以在这里打好包，拷到 Windows 仓库根解压，再跑 scripts\verify-windows.ps1。
# 顺带捎上 godot-xterm 插件，Windows 那边就能 -Offline 跑。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/m0/run/windows-bundle.zip}"
cd "$ROOT"

files=(m0/images/vmlinuz-virt m0/images/m0-guest.cpio.gz m0/images/alpine-main.qcow2)
for f in "${files[@]}"; do
  [[ -f "$f" ]] || { echo "缺少 $f —— 先跑 m0/scripts/00、01、02" >&2; exit 1; }
done
[[ -d src/GameHacker.Godot/addons/godot_xterm ]] && files+=(src/GameHacker.Godot/addons/godot_xterm)

rm -f "$OUT"
zip -qr "$OUT" "${files[@]}"
echo "已打包: $OUT ($(du -h "$OUT" | cut -f1))"
echo "拷到 Windows 仓库根解压，然后："
echo "  powershell -ExecutionPolicy Bypass -File scripts\\verify-windows.ps1"
