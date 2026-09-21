#!/usr/bin/env bash
# 烤目标机 initramfs：Alpine 的 initramfs-virt 当素材，换掉 /init。
# 自带 busybox（ip/ping/vi）和 virtio_net，约 2 秒起，不需要磁盘、不需要 modloop。
# 额外补入 ext4 —— 主角机要靠它从可写磁盘 switch_root。
source "$(dirname "$0")/lib.sh"
need cpio; need gzip; need depmod; need modprobe

SRC="$IMAGES/initramfs-virt"
OUT="$IMAGES/m0-guest.cpio.gz"
WORK="$RUN/initramfs-build"
KMODS="$RUN/kmods"
EXTRA_MODULES="${EXTRA_MODULES:-ext4}"

[[ -f "$SRC" ]] || die "缺少 $SRC，先跑 00-fetch-images.sh"

rm -rf "$WORK"; mkdir -p "$WORK"
log "解包 Alpine initramfs 素材"
( cd "$WORK" && zcat "$SRC" | cpio -idm --quiet )

KVER="$(ls "$WORK/usr/lib/modules" | head -1)"
log "内核版本 $KVER"

if [[ -n "$EXTRA_MODULES" ]]; then
  # initramfs-virt 只带启动必需的驱动，ext4 之类在 modloop 里。
  # 从 apk 的 linux-virt 包取，版本必然与 vmlinuz-virt 一致。
  if [[ ! -d "$KMODS/lib/modules/$KVER" ]]; then
    log "取 linux-virt 模块树（缓存到 $KMODS）"
    rm -rf "$KMODS"
    # mkinitfs 触发器会在假根里失败，无害，模块已经落盘
    m0_apk_newroot "$KMODS" linux-virt >/dev/null 2>&1 || true
    [[ -d "$KMODS/lib/modules/$KVER" ]] || die "linux-virt 里没有 $KVER 的模块树"
  fi

  for m in $EXTRA_MODULES; do
    log "补入 $m 及其依赖"
    while read -r ko; do
      dst="$WORK/usr/lib/${ko#"$KMODS"/lib/}"
      mkdir -p "$(dirname "$dst")"
      cp -n "$ko" "$dst" && echo "    $(basename "$ko")"
    done < <(modprobe --show-depends -d "$KMODS" -S "$KVER" "$m" | awk '/^insmod/ {print $2}')
  done

  log "重建 modules.dep"
  depmod -b "$WORK" "$KVER"
fi

log "装入 m0/guest/{init,common.sh}"
install -m 0755 "$M0_ROOT/guest/init"      "$WORK/init"
install -m 0644 "$M0_ROOT/guest/common.sh" "$WORK/etc/m0-common.sh"

log "重新打包 -> $OUT"
( cd "$WORK" && find . -print0 | cpio --null -o -H newc --quiet ) | gzip -9 > "$OUT"
ls -lh "$OUT" | awk '{print "  " $5 "  " $9}'
log "完成"
