# M0 客户机公共启动逻辑。
# 被 initramfs 的 /init 和 Alpine rootfs 的 /sbin/m0-init 共同 source，
# 保证两种客户机的网络配置、控制通道协议、终端行为完全一致。
#
# 依赖调用方已经挂好 /proc /sys /dev 并设好 PATH。

m0_cmdline_get() {
    # $1 = 键名（如 m0.ip），从 /proc/cmdline 取值
    for _x in $(cat /proc/cmdline); do
        case "$_x" in "$1="*) echo "${_x#$1=}"; return;; esac
    done
}

m0_setup_net() {
    M0_HOST="$(m0_cmdline_get m0.host)"; : "${M0_HOST:=m0}"
    M0_IP="$(m0_cmdline_get m0.ip)"
    hostname "$M0_HOST"
    echo "$M0_HOST" > /etc/hostname
    ip link set lo up
    ip link set eth0 up 2>/dev/null
    [ -n "$M0_IP" ] && ip addr add "$M0_IP" dev eth0 2>/dev/null
    return 0
}

# 隐藏控制通道（ttyS1）。协议：宿主发一行命令，客户机回一行 JSON 事件。
# resize 必须走这里 —— 裸串口不是 PTY，没有 TIOCSWINSZ 带外信令。
m0_start_agent() {
    [ -c /dev/ttyS1 ] || return 0
    stty -F /dev/ttyS1 raw -echo 2>/dev/null
    echo "{\"ev\":\"ready\",\"host\":\"$M0_HOST\",\"ip\":\"$M0_IP\"}" > /dev/ttyS1
    (
        while read -r line < /dev/ttyS1; do
            case "$line" in
                resize\ *) set -- $line
                           stty -F /dev/ttyS0 rows "$2" cols "$3" 2>/dev/null
                           echo "{\"ev\":\"resize\",\"rows\":$2,\"cols\":$3}" > /dev/ttyS1 ;;
                ping\ *)   set -- $line
                           if ping -c1 -W2 "$2" >/dev/null 2>&1; then R=ok; else R=fail; fi
                           echo "{\"ev\":\"ping\",\"target\":\"$2\",\"result\":\"$R\"}" > /dev/ttyS1 ;;
                status)    echo "{\"ev\":\"status\",\"host\":\"$M0_HOST\",\"up\":1}" > /dev/ttyS1 ;;
            esac
        done
    ) &
}

# tmux / script / 任何要开子终端的程序都需要 pty。
# devtmpfs 不会自动建 /dev/pts 目录，不先 mkdir 的话 mount 会失败，
# 症状是 tmux 报 "create window failed: fork failed: No such file or directory"。
m0_mount_pty() {
    mkdir -p /dev/pts /dev/shm
    mountpoint -q /dev/pts || mount -t devpts devpts /dev/pts || echo "!! devpts 挂载失败"
    mountpoint -q /dev/shm || mount -t tmpfs  tmpfs  /dev/shm 2>/dev/null
    # tmux 从 $SHELL 决定开什么 shell，不设的话它去查 passwd
    SHELL=/bin/sh; export SHELL
}

m0_banner() {
    echo
    echo "  === M0 guest: $M0_HOST ==="
    ip -o -4 addr show 2>/dev/null | sed 's/^/  /'
    echo
}

# getty 而不是直接 exec sh：它给出真正的控制终端（job control）并设好 TERM，
# 这是 tmux/vim 正常工作的前提。外层 while 循环 respawn ——
# 玩家在游戏里敲 exit 不能让整台虚拟机 panic。
m0_console_loop() {
    while true; do
        getty -n -l /bin/sh 115200 ttyS0 xterm-256color
        sleep 1
    done
}
