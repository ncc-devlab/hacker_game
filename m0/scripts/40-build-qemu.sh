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

# --without-default-features 把所有可选特性默认关掉，所以这里只列要开回来的。
# 注意 zlib 不是 configure 选项 —— QEMU 把它当硬依赖，由 pkg-config 找。
CONFIGURE_ARGS=(
  --prefix="$PREFIX"
  --target-list=x86_64-softmmu     # 只要一个 target
  --without-default-features       # 先全关，再按需开回
  --enable-tcg                     # 性能基准，兑现「不要求 VT-x」
  --enable-tools                   # qemu-img：存档用的 qcow2 overlay 靠它创建，要随游戏发行
  --enable-pixman                  # QEMU 显示子系统的硬依赖，即便我们 -display none
  --disable-werror
)
# 注意不能加 --disable-install-blobs：即便用 -kernel 直接引导内核，
# q35 仍然要 SeaBIOS 来完成装载，缺了会报 "could not load PC BIOS 'bios-256k.bin'"。
# 显式没有开的（因此是关的）：
#   gtk / sdl / vnc / spice / curses —— 任何 UI 后端都不需要，我们只用串口
#   alsa / oss / pa / jack          —— 不需要音频
#   slirp                            —— 用户态 NAT，我们有自研交换机
#   guest-agent                      —— 判定走自研的 ttyS1 协议，不用 QGA
#   libusb / smartcard / png / docs  —— 与玩法无关

# 整棵树编译一次约五分钟，所以默认增量。改了 CONFIGURE_ARGS 或想从头来时用 CLEAN=1。
if [[ -n "${CLEAN:-}" || ! -f "$BUILD/build.ninja" ]]; then
  log "configure（$JOBS 并行）"
  rm -rf "$BUILD"; mkdir -p "$BUILD"
  ( cd "$BUILD" && "$SRC/configure" "${CONFIGURE_ARGS[@]}" ) || die "configure 失败"
else
  log "复用已有 build 目录（CLEAN=1 可强制重来）"
fi

log "编译"
ninja -C "$BUILD" -j "$JOBS" || die "编译失败"

log "安装到 $PREFIX"
rm -rf "$PREFIX"
ninja -C "$BUILD" install >/dev/null

# --- 发行裁剪 ---------------------------------------------------------
# configure 的开关只决定编译进什么，装出来的东西还要再筛一遍。
log "strip 符号"
find "$PREFIX/bin" -type f -exec strip --strip-unneeded {} + 2>/dev/null || true

log "删掉发行用不到的部分"
# 只留这两个：模拟器本体，以及创建存档用 qcow2 overlay 的 qemu-img
for f in "$PREFIX"/bin/*; do
  case "$(basename "$f")" in
    qemu-system-x86_64|qemu-img) ;;
    *) rm -f "$f" ;;
  esac
done
rm -rf "$PREFIX/libexec"                  # bridge-helper：要提权的桥接，我们不用
rm -rf "$PREFIX/share/applications" "$PREFIX/share/icons"   # 桌面集成，无关
rm -rf "$PREFIX/share/doc" "$PREFIX/share/man"

# 固件用白名单而不是黑名单：install 会装上全部架构的固件，
# 光 edk2 的 arm/aarch64/riscv/loongarch UEFI 镜像就有 313MB，
# 而我们只跑 x86_64、且是 SeaBIOS + -kernel 直接引导，完全不碰 UEFI。
log "固件白名单"
FIRMWARE_KEEP=(
  bios-256k.bin        # SeaBIOS，q35 的默认 BIOS。即便 -kernel 直接引导也要它来装载
  kvmvapic.bin         # pc/q35 默认加载
  linuxboot_dma.bin    # -kernel 走 DMA 快速装载用的 option ROM
  vgabios-stdvga.bin   # 概要书里开局那台 FreeDOS 需要 VGA 文本模式，不是串口
)
# efi-virtio.rom 不在白名单里：那是 virtio-net-pci 的 PXE 引导 ROM，
# 我们永远不网络引导，改用 romfile= 把它关掉（见 QemuLauncher / m0lib）。
FW="$PREFIX/share/qemu"
if [[ -d "$FW" ]]; then
  KEEP_RE="$(IFS='|'; echo "${FIRMWARE_KEEP[*]}")"
  find "$FW" -mindepth 1 -maxdepth 1 | grep -vE "/($KEEP_RE)$" | xargs -r rm -rf
fi

echo
log "体积对比（发行要自带全部依赖，所以闭包比二进制本身更重要）"
closure() { ldd "$1" 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i ~ /^\//) print $i}' \
            | sort -u | xargs -r du -cb 2>/dev/null | tail -1 | cut -f1; }
report() {
  printf '  %-10s 二进制 %6s   依赖 %2s 个 .so / %6s\n' "$1" \
    "$(du -h "$2" | cut -f1)" "$(ldd "$2" 2>/dev/null | grep -c '=>')" \
    "$(numfmt --to=iec "$(closure "$2")")"
}
SYS="$(command -v qemu-system-x86_64 || true)"
OURS="$PREFIX/bin/qemu-system-x86_64"
[[ -n "$SYS" ]] && report "发行版" "$SYS"
report "裁剪版" "$OURS"
printf '  裁剪版发行目录合计 %s\n' "$(du -sh "$PREFIX" | cut -f1)"

echo
log "回归验证（用裁剪版重跑 M0-b）"
QEMU="$OURS" bash "$M0_ROOT/scripts/20-switched.sh" | tail -4
