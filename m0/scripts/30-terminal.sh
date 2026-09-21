#!/usr/bin/env bash
# M0-c：终端渲染验收（vim / tmux / ANSI / TERM / resize）。详见 m0/probe/run_terminal.py
source "$(dirname "$0")/lib.sh"
[[ -f "$IMAGES/alpine-main.qcow2" ]] || die "缺 Alpine 镜像，先跑 02-build-alpine-rootfs.sh"
exec "$(m0_venv_python)" "$M0_ROOT/probe/run_terminal.py" "$@"
