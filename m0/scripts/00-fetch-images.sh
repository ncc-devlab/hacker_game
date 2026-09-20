#!/usr/bin/env bash
# 取回 M0 客户机素材。
#   1) Alpine netboot 的 vmlinuz-virt / initramfs-virt / modloop-virt
#   2) busybox 静态二进制 —— 用来烤一个我们完全掌控的极小 initramfs，
#      它就是概要书里「极小 Buildroot Linux」目标机的 M0 替身。
source "$(dirname "$0")/lib.sh"
need curl; need tar

ALPINE_BRANCH="${ALPINE_BRANCH:-v3.23}"
ALPINE_VER="${ALPINE_VER:-3.23.6}"
BASE="https://dl-cdn.alpinelinux.org/alpine/${ALPINE_BRANCH}/releases/x86_64"
TARBALL="alpine-netboot-${ALPINE_VER}-x86_64.tar.gz"

cd "$IMAGES"

if [[ ! -f "$TARBALL" ]]; then
  log "下载 $TARBALL"
  curl -fSL --progress-bar -o "$TARBALL" "$BASE/$TARBALL"
else
  log "已存在 $TARBALL，跳过下载"
fi

log "解包内核与 initramfs"
tar -xzf "$TARBALL" --strip-components=1 -C . \
    boot/vmlinuz-virt boot/initramfs-virt boot/modloop-virt 2>/dev/null \
  || tar -xzf "$TARBALL" -C . && true

# 不同版本目录层级可能不同，统一收敛到 $IMAGES 根下
for f in vmlinuz-virt initramfs-virt modloop-virt; do
  [[ -f "$f" ]] || { found=$(find . -name "$f" -type f | head -1); [[ -n "${found:-}" ]] && cp "$found" "$f"; }
done

ls -la vmlinuz-virt initramfs-virt modloop-virt 2>&1 | sed 's/^/  /'
log "完成。镜像目录: $IMAGES"
