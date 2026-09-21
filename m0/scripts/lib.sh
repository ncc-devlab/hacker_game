#!/usr/bin/env bash
# M0 公共定义。所有脚本 source 这个文件。
set -euo pipefail

M0_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGES="$M0_ROOT/images"
RUN="$M0_ROOT/run"
mkdir -p "$IMAGES" "$RUN"

# 允许用环境变量覆盖，指向我们自己裁剪构建的 QEMU
QEMU="${QEMU:-qemu-system-x86_64}"
QEMU_IMG="${QEMU_IMG:-qemu-img}"

# 端口分配：全部走 127.0.0.1 TCP，这是三端唯一可移植的选择。
# 不要用 AF_UNIX —— Windows 上的 QEMU 不保证支持。
#   17x01 ttyS0 终端      17x02 ttyS1 隐藏控制通道      17x03 QMP
PORT_CON() { echo $((17000 + $1 * 100 + 1)); }
PORT_CTL() { echo $((17000 + $1 * 100 + 2)); }
PORT_QMP() { echo $((17000 + $1 * 100 + 3)); }
PORT_NET=17900   # 交换机 / 探针 / 对端监听口

# MAC 按机位固定，方便在抓包里一眼认出是谁
MAC_OF() { printf '52:54:00:00:00:%02x' "$1"; }

log()  { printf '\033[36m[m0]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31m[m0] 错误:\033[0m %s\n' "$*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "缺少命令: $1"; }

# --- Alpine 素材与 apk ---------------------------------------------------
# apk 是 musl 动态链接的，用 minirootfs 自带的 loader 就能在宿主上直接跑，
# 全程不需要 root、不需要 chroot。01 和 02 都依赖这套。

ALPINE_BRANCH="${ALPINE_BRANCH:-v3.23}"
ALPINE_VER="${ALPINE_VER:-3.23.6}"
ALPINE_CDN="${ALPINE_CDN:-https://dl-cdn.alpinelinux.org/alpine}"
MINIROOTFS_TGZ="alpine-minirootfs-${ALPINE_VER}-x86_64.tar.gz"

m0_fetch() {  # $1 = 相对 CDN 的路径, $2 = 落地文件名
  local url="$ALPINE_CDN/$1" dst="$IMAGES/$2"
  [[ -f "$dst" ]] && return 0
  log "下载 $2"
  curl -fsSL -o "$dst" "$url"
}

m0_minirootfs() {  # 输出 minirootfs 目录路径，按需展开
  local dir="$RUN/minirootfs"
  if [[ ! -x "$dir/sbin/apk" ]]; then
    m0_fetch "$ALPINE_BRANCH/releases/x86_64/$MINIROOTFS_TGZ" "$MINIROOTFS_TGZ"
    rm -rf "$dir"; mkdir -p "$dir"
    tar -xzf "$IMAGES/$MINIROOTFS_TGZ" -C "$dir"
  fi
  echo "$dir"
}

m0_apk() {  # 用法: m0_apk --root <目标> add <包...>
  local mr; mr="$(m0_minirootfs)"
  "$mr/lib/ld-musl-x86_64.so.1" --library-path "$mr/lib:$mr/usr/lib" \
    "$mr/sbin/apk" --arch x86_64 --no-interactive "$@"
}

m0_apk_newroot() {  # 在空目录里建一个可用的 apk 根（含仓库与签名公钥）
  local root="$1" mr; mr="$(m0_minirootfs)"
  mkdir -p "$root/etc/apk"
  printf '%s/%s/main\n%s/%s/community\n' \
    "$ALPINE_CDN" "$ALPINE_BRANCH" "$ALPINE_CDN" "$ALPINE_BRANCH" > "$root/etc/apk/repositories"
  cp -r "$mr/etc/apk/keys" "$root/etc/apk/"
  # apk 3.x 非 root 建库必须给 --usermode
  m0_apk --root "$root" --usermode --initdb add "${@:2}"
}

# --- 宿主侧 Python 环境 --------------------------------------------------
# M0-c 要在宿主上真实渲染客户机的 ANSI 字节流，需要 pyte。
m0_venv_python() {
  local venv="$RUN/venv"
  if [[ ! -x "$venv/bin/python" ]]; then
    log "创建 venv 并安装 $M0_ROOT/requirements.txt" >&2
    python3 -m venv "$venv" >&2
    "$venv/bin/pip" install -q -r "$M0_ROOT/requirements.txt" >&2
  fi
  echo "$venv/bin/python"
}
