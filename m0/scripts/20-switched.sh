#!/usr/bin/env bash
# M0-b：两台 VM 经自研交换机互通。详见 m0/probe/run_switched.py
source "$(dirname "$0")/lib.sh"
[[ -f "$IMAGES/m0-guest.cpio.gz" ]] || die "缺镜像，先跑 00- 和 01- 脚本"
exec python3 "$M0_ROOT/probe/run_switched.py" "$@"
