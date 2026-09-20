#!/usr/bin/env bash
# 用 Alpine 的 initramfs-virt 当素材，烤一个我们完全掌控的极小客户机 initramfs。
# 它自带 busybox（含 ip/ping/vi）和 virtio_net.ko，启动约 1.5 秒，不需要磁盘、不需要 modloop。
# 这是概要书里「极小 Buildroot Linux」目标机在 M0 阶段的替身。
source "$(dirname "$0")/lib.sh"
need cpio; need gzip

SRC="$IMAGES/initramfs-virt"
INIT="$M0_ROOT/guest/init"
OUT="$IMAGES/m0-guest.cpio.gz"
WORK="$RUN/initramfs-build"

[[ -f "$SRC"  ]] || die "缺少 $SRC，先跑 00-fetch-images.sh"
[[ -f "$INIT" ]] || die "缺少 $INIT"

rm -rf "$WORK"; mkdir -p "$WORK"
log "解包 Alpine initramfs 素材"
( cd "$WORK" && zcat "$SRC" | cpio -idm --quiet )

log "装入 m0/guest/init"
install -m 0755 "$INIT" "$WORK/init"

log "重新打包 -> $OUT"
( cd "$WORK" && find . -print0 | cpio --null -o -H newc --quiet ) | gzip -9 > "$OUT"

ls -lh "$OUT" | awk '{print "  " $5 "  " $9}'
log "完成"
