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
# e1000e 是伪装用的：virtio 网卡的 PCI ID 是 0x1af4（Red Hat），
# virtio 盘挂出来叫 /dev/vda，两样都直接写着「我是虚拟机」。
# 换成 Intel 82574L（0x8086）+ AHCI（/dev/sda）就看不出来了。
# ahci 是内核内建的，不用单独收。
# ahci/libata 是内核内建的，但 sd_mod 不是 —— 少了它 SATA 盘
# 只会在 dmesg 里被 libata 认出来，永远不出现 /dev/sda。
EXTRA_MODULES="${EXTRA_MODULES:-ext4 e1000e sd_mod}"
# 额外装进来的软件包，只拷包本身的文件，不带依赖 —— 依赖得是 initramfs 里本来就有的
# （sudo 要的 musl、zlib 都在）。sudo：管理员改别人口令、玩家侦察 sudo -l 都靠它；
# busybox 没有 setuid，它的 su / passwd 在普通账号下用不了。
EXTRA_PACKAGES="${EXTRA_PACKAGES:-sudo}"
PKGROOT="$RUN/pkgroot"

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

if [[ -n "$EXTRA_PACKAGES" ]]; then
  for p in $EXTRA_PACKAGES; do
    [[ -d "$PKGROOT/lib/apk/db" ]] && grep -qx "P:$p" "$PKGROOT/lib/apk/db/installed" && continue
    log "取软件包 $EXTRA_PACKAGES（缓存到 $PKGROOT）"
    rm -rf "$PKGROOT"
    m0_apk_newroot "$PKGROOT" $EXTRA_PACKAGES >/dev/null
    break
  done

  for p in $EXTRA_PACKAGES; do
    log "装入 $p"
    # installed 库按空行分段，每段一个包：F: 是目录，R: 是其下的文件
    awk -v pkg="$p" 'BEGIN { RS = ""; FS = "\n" }
      { name = ""; for (i = 1; i <= NF; i++) if ($i ~ /^P:/) name = substr($i, 3)
        if (name != pkg) next
        for (i = 1; i <= NF; i++) {
          if ($i ~ /^F:/) dir = substr($i, 3)
          else if ($i ~ /^R:/) print dir "/" substr($i, 3)
        } }' "$PKGROOT/lib/apk/db/installed" |
    while read -r f; do
      mkdir -p "$WORK/$(dirname "$f")"
      cp -a "$PKGROOT/$f" "$WORK/$f"
    done
  done
  # 属主和 setuid 位在这里给不了（非 root 打包），开机时由 m0_setup_sudo 补上
fi

log "装入 m0/guest/{init,common.sh}"
install -m 0755 "$M0_ROOT/guest/init"      "$WORK/init"
# 放 /lib/m0 而不是 /etc：玩家 ls /etc 一眼就能看见 m0-common.sh，穿帮。
# 启动末尾 m0_disguise 还会往这个目录上盖一层空 tmpfs，本次会话里它是空的。
install -d "$WORK/lib/m0"
install -m 0644 "$M0_ROOT/guest/common.sh" "$WORK/lib/m0/common.sh"

log "重新打包 -> $OUT"
( cd "$WORK" && find . -print0 | cpio --null -o -H newc --quiet ) | gzip -9 > "$OUT"
ls -lh "$OUT" | awk '{print "  " $5 "  " $9}'
log "完成"
