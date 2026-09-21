#!/usr/bin/env bash
# M0-e：裁剪构建 QEMU。
#
# 注意这是「裁剪」不是「魔改」—— 只动 configure 开关，不改一行源码。
# 项目硬约束是不 fork QEMU（理由见 m0/README.md），裁剪的目的只有两个：
#   1) 砍分发体积（Steam depot 按平台分发，每端只带一份）
#   2) 砍杀软误报面积（少一个 UI 后端就少一堆可疑导入）
#
# 我们实际需要的能力面很窄：
#   TCG（不要求 VT-x）、x86_64 target、virtio 设备、qcow2、
#   chardev socket、-netdev stream、QMP。
# 不需要：任何图形/音频后端、slirp（我们有自研交换机）、
#         user-mode emulation、绝大多数非 virtio 设备模型。
source "$(dirname "$0")/lib.sh"
need meson; need ninja; need gcc; need python3

TARBALL="${TARBALL:-$M0_ROOT/../third_party/qemu-11.1.1.tar.xz}"
SRC="$RUN/qemu-src"
BUILD="$RUN/qemu-build"
PREFIX="${PREFIX:-$M0_ROOT/../runtime/linux-x86_64}"
JOBS="${JOBS:-$(nproc)}"

[[ -f "$TARBALL" ]] || die "找不到 $TARBALL"

if [[ ! -f "$SRC/configure" ]]; then
  log "解包 $(basename "$TARBALL")"
  rm -rf "$SRC"; mkdir -p "$SRC"
  tar -xf "$TARBALL" -C "$SRC" --strip-components=1
fi

CONFIGURE_ARGS=(
  --prefix="$PREFIX"
  --target-list=x86_64-softmmu     # 只要一个 target
  --without-default-features       # 先全关，再按需开回
  --enable-tcg                     # 性能基准，兑现「不要求 VT-x」
  --enable-system
  --disable-user
  --enable-zlib                    # qcow2 压缩需要
  --enable-pixman                  # QEMU 显示子系统的硬依赖，即便我们 -display none
  --disable-slirp                  # 用户态 NAT，我们有自研交换机
  --disable-docs
  --disable-guest-agent
  --disable-install-blobs
  --disable-werror
)

log "configure（$JOBS 并行）"
rm -rf "$BUILD"; mkdir -p "$BUILD"
( cd "$BUILD" && "$SRC/configure" "${CONFIGURE_ARGS[@]}" ) || die "configure 失败"

log "编译"
ninja -C "$BUILD" -j "$JOBS" || die "编译失败"

log "安装到 $PREFIX"
ninja -C "$BUILD" install >/dev/null

echo
log "体积对比"
SYS="$(command -v qemu-system-x86_64 || true)"
OURS="$PREFIX/bin/qemu-system-x86_64"
[[ -n "$SYS"  ]] && printf '  发行版版本  %8s  %s\n' "$(du -h "$SYS"  | cut -f1)" "$SYS"
printf '  裁剪版本    %8s  %s\n' "$(du -h "$OURS" | cut -f1)" "$OURS"
printf '  裁剪版整体  %8s  %s\n' "$(du -sh "$PREFIX" | cut -f1)" "$PREFIX"
echo
log "用裁剪版跑一遍 M0-b 回归："
log "  QEMU=$OURS bash m0/scripts/20-switched.sh"
