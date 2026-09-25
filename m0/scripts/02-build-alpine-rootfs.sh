#!/usr/bin/env bash
# 造主角机镜像：Alpine 完整 rootfs + vim/tmux，落成 qcow2。
# 全程不需要 root：
#   * apk 通过 musl loader 直接在宿主上跑，--root 指向目标目录
#   * mke2fs -d 直接从目录生成 ext4，不用 mount
#   * 有 fakeroot 就用它，让文件属主正确落成 0:0
source "$(dirname "$0")/lib.sh"
need curl; need tar; need mke2fs; need "$QEMU_IMG"

ALPINE_BRANCH="${ALPINE_BRANCH:-v3.23}"
ALPINE_VER="${ALPINE_VER:-3.23.6}"
PKGS="${PKGS:-alpine-baselayout busybox musl-utils vim tmux ncurses ncurses-terminfo iproute2 iputils}"
MR="alpine-minirootfs-${ALPINE_VER}-x86_64.tar.gz"
CDN="https://dl-cdn.alpinelinux.org/alpine"

ROOT="$RUN/alpine-root"
RAW="$RUN/alpine-root.img"
# OUT 可用环境变量覆盖：镜像正被运行中的 QEMU 锁着时，可以先烤到别处再 mv 覆盖，
# 不必先关游戏（运行中的实例继续用旧 inode，新文件留给下次启动）。
OUT="${OUT:-$IMAGES/alpine-main.qcow2}"

cd "$IMAGES"
[[ -f "$MR" ]] || { log "下载 $MR"; curl -fsSL -o "$MR" "$CDN/$ALPINE_BRANCH/releases/x86_64/$MR"; }

log "展开 minirootfs -> $ROOT"
rm -rf "$ROOT"; mkdir -p "$ROOT"
tar -xzf "$MR" -C "$ROOT"

# apk 是 musl 动态链接的，用 rootfs 自带的 loader 在宿主上跑它
APK=( "$ROOT/lib/ld-musl-x86_64.so.1" --library-path "$ROOT/lib:$ROOT/usr/lib" "$ROOT/sbin/apk" )

mkdir -p "$ROOT/etc/apk"
cat > "$ROOT/etc/apk/repositories" <<EOF
$CDN/$ALPINE_BRANCH/main
$CDN/$ALPINE_BRANCH/community
EOF

log "安装: $PKGS"
"${APK[@]}" --root "$ROOT" --arch x86_64 --no-interactive update >/dev/null
"${APK[@]}" --root "$ROOT" --arch x86_64 --no-interactive add $PKGS

log "装入 stage2 与公共启动逻辑"
# 装成 /sbin/init 而不是 /sbin/m0-init：真机上就该有这个文件，
# 叫 m0-init 的话玩家 ls /sbin 一眼就看出来这是个被改过的系统。
install -m 0755 "$M0_ROOT/guest/stage2"    "$ROOT/sbin/init"
install -d "$ROOT/lib/m0"
install -m 0644 "$M0_ROOT/guest/common.sh" "$ROOT/lib/m0/common.sh"
rm -f "$ROOT/etc/m0-common.sh" "$ROOT/sbin/m0-init"

# 终端相关的最小配置：TERM 由 getty 设置，这里只保证 vim/tmux 有像样的默认行为
cat > "$ROOT/root/.vimrc" <<'EOF'
set nocompatible
syntax on
set number ruler laststatus=2
set ttyfast
EOF
cat > "$ROOT/root/.tmux.conf" <<'EOF'
set -g default-terminal "screen-256color"
set -g status-left "[m0] "
set -g mouse off
EOF

SIZE_MB=$(( $(du -sm "$ROOT" | cut -f1) + 120 ))
log "生成 ext4 镜像 (${SIZE_MB}M)"
rm -f "$RAW"
MKE2FS=(mke2fs -q -F -t ext4 -L m0root -d "$ROOT" -E root_owner=0:0 "$RAW" "${SIZE_MB}m")
if command -v fakeroot >/dev/null; then fakeroot "${MKE2FS[@]}"; else
  log "没有 fakeroot：镜像内文件属主会是宿主 uid（客户机以 root 运行，M0 不影响）"
  "${MKE2FS[@]}"
fi

log "转 qcow2 -> $OUT"
"$QEMU_IMG" convert -f raw -O qcow2 -c "$RAW" "$OUT"
rm -f "$RAW"
ls -lh "$OUT" | awk '{print "  " $5 "  " $9}'
log "完成"
