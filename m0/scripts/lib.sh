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
