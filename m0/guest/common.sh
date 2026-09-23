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
    # 多网卡（跳板机一脚在外网 VLAN、一脚在内网 VLAN）：m0.ip1 配给 eth1，以此类推。
    # 宿主侧最多给 4 块网卡（QemuLauncher.MaxNics）
    for _n in 1 2 3; do
        _ip="$(m0_cmdline_get m0.ip$_n)"
        [ -n "$_ip" ] || continue
        ip link set "eth$_n" up 2>/dev/null
        ip addr add "$_ip" dev "eth$_n" 2>/dev/null
    done
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

# 一台真实的机器上总有些东西在跑、在记。
#
# 管理员查岗查的就是这些：日志里有没有奇怪的记录、该在的服务还在不在、
# 谁登录过。所以这些东西必须是<b>真的</b> —— 玩家能看、能改、能删，
# 也正因为能改，才有「掩盖痕迹」这一步可玩。
m0_start_services() {
    # 注意调用顺序：这一步要在建管理员账号<b>之后</b>。反过来的话，
    # chpasswd 会往 syslog 写一句「口令已修改」，等于告诉玩家这机器上有个什么账号
    mkdir -p /var/log /var/run /var/empty
    # 登录记录要有这个文件才会被写：login 只往已经存在的 wtmp 里追加
    [ -f /var/log/wtmp ] || : > /var/log/wtmp
    # 系统日志。-n 前台跑，交给我们自己放后台，免得它 fork 之后 pid 不受控
    syslogd -n >/dev/null 2>&1 &
    sleep 0.2
    logger -t init "system boot: $M0_HOST"
}

# 管理员专用 tty（ttyS2）。
#
# 游戏侧的「管理员」是个假人，但他登录这台机器的方式是真的：真的 getty、
# 真的 login、真的账号。于是他留下的痕迹也全是真的 —— utmp/wtmp 里有会话、
# ps 里有他的 shell、他改过的文件就是被改过。玩家能察觉管理员来过，
# 这正是 MVP2 想要的对手感。
#
# 和 ttyS1 的分工：ttyS1 是给判定用的隐藏通道，玩家看不见也碰不到；
# ttyS2 是世界之内的东西，玩家在 ps / who 里看得到它。
#
# 账号和口令经内核 cmdline 传进来（m0.admin=用户名:口令）。cmdline 本身
# 已经被 m0_disguise 盖掉了，玩家读不到真的那份。
m0_start_admin_tty() {
    [ -c /dev/ttyS2 ] || return 0
    M0_ADMIN="$(m0_cmdline_get m0.admin)"
    [ -n "$M0_ADMIN" ] || return 0

    _user="${M0_ADMIN%%:*}"
    _pw="${M0_ADMIN#*:}"
    if ! grep -q "^$_user:" /etc/passwd 2>/dev/null; then
        # 家目录要自己建：initramfs 里没有 /home，adduser 建不出来，
        # 登录时 login 会甩一句 "can't change directory"，一眼就不像常驻账号
        mkdir -p "/home/$_user"
        adduser -D -h "/home/$_user" -s /bin/sh "$_user" >/dev/null 2>&1
        echo "$_user:$_pw" | chpasswd >/dev/null 2>&1
        chown -R "$_user" "/home/$_user" 2>/dev/null
    fi

    # -L 不等载波；vt100 让 login 之后的 shell 知道终端类型。
    # setsid 是必须的：getty 要自己当会话首进程才能把 tty 变成控制终端。
    # 要循环重启：管理员查完岗会 exit，getty 也跟着结束，
    # 不重开的话他这辈子只能登录一次（真机上这是 init 的活）
    ( while : ; do
        setsid getty -L 115200 ttyS2 vt100 >/dev/null 2>&1
        sleep 1
      done ) &
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

# ---------------------------------------------------------------------------
# 伪装。默认配置下客户机到处写着「我是虚拟机」，玩家随手 uname -r 就穿帮。
#
# 分工：主板 / BIOS / CPU 型号在宿主侧用 QEMU 的 -smbios / -cpu 做掉
#       （见 GameHacker.Core 的 HardwarePersona），这里只管内核和发行版那一层。
#
# 手段是 bind-mount 覆盖 /proc 下的单个文件 —— procfs 支持这么干，
# 实测 /proc/version 和 /proc/cmdline 都能盖住，umount 之后原样恢复。
#
# ⚠ 必须在所有 m0_cmdline_get 调用之后再跑：盖掉 /proc/cmdline 以后就读不到了。
# ---------------------------------------------------------------------------

# cmdline 按空格分词，值里不能带空格，宿主侧把空格换成了 ~，这里还原。
m0_unesc() { printf '%s' "$1" | tr '~' ' '; }

m0_disguise() {
    # 真实值先留一份 —— 下面要用它在 /proc/version 里做替换，
    # 而且 m0_install_uname 之后就问不到了。
    M0_REAL_R="$(uname -r)"

    M0_UTS_R="$(m0_unesc "$(m0_cmdline_get m0.uts_r)")"
    M0_FLAVOR="$(m0_cmdline_get m0.uts_flavor)"
    # 不写死版本号，只把 Alpine 的 -virt 后缀换成 -lts：
    # 写死的话每次升级客户机内核都要跟着改，迟早对不上。
    if [ -z "$M0_UTS_R" ] && [ -n "$M0_FLAVOR" ]; then
        M0_UTS_R="$(printf '%s' "$M0_REAL_R" | sed "s/-virt\$/-$M0_FLAVOR/")"
    fi
    [ -n "$M0_UTS_R" ] || return 0            # 没给人设 = 不伪装

    M0_UTS_V="$(m0_unesc "$(m0_cmdline_get m0.uts_v)")"
    [ -n "$M0_UTS_V" ] || M0_UTS_V="$(uname -v)"
    M0_FAKECMD="$(m0_unesc "$(m0_cmdline_get m0.cmdline)")"
    M0_OS_NAME="$(m0_unesc "$(m0_cmdline_get m0.os_name)")"
    M0_OS_ID="$(m0_cmdline_get m0.os_id)"
    M0_OS_VER="$(m0_cmdline_get m0.os_ver)"

    mkdir -p /run/m0
    cat > /run/m0/persona <<PERSONA
M0_UTS_R="$M0_UTS_R"
M0_UTS_V="$M0_UTS_V"
PERSONA

    # 1. /proc/version —— uname 的壳子挡不住 cat 这个文件。
    #    在真的那行上做替换，而不是自己拼一行：编译器版本、构建者这些
    #    保持原样才不会和别处对不上。
    sed "s|$M0_REAL_R|$M0_UTS_R|g" /proc/version > /run/m0/version 2>/dev/null
    [ -s /run/m0/version ] || printf 'Linux version %s\n' "$M0_UTS_R" > /run/m0/version
    mount --bind /run/m0/version /proc/version 2>/dev/null

    # 2. /proc/cmdline —— 真机上没人会看见 console=ttyS0 和一堆 m0.*
    if [ -n "$M0_FAKECMD" ]; then
        printf '%s\n' "$M0_FAKECMD" > /run/m0/cmdline
        mount --bind /run/m0/cmdline /proc/cmdline 2>/dev/null
    fi

    # 3. uname 本体。utsname 是内核编译期定死的、用户态改不了，
    #    所以把 /bin/uname 从 busybox 的软链换成我们自己的壳子。
    #    绕得过去的只有 `busybox uname` 这种直呼 applet 的写法 ——
    #    要根治得换自建内核，见 docs/伪装.md。
    m0_install_uname

    # 4. 内核环形缓冲区。console 上已经用 quiet/loglevel 压掉了，
    #    但 dmesg 读的是缓冲区本身，里面还留着 DMI、SeaBIOS、内核版本。
    #    busybox 的 dmesg 没有 -C，用 -c（打印后清空）把输出丢掉即可。
    dmesg -c >/dev/null 2>&1

    # 5. 发行版标识。默认不动 —— 这套客户机是货真价实的 Alpine + busybox，
    #    硬说自己是 Ubuntu 反而处处对不上（没有 dpkg、/etc/apk 还在）。
    #    要做架空发行版时再从宿主侧传这几个值。
    if [ -n "$M0_OS_NAME" ]; then
        cat > /etc/os-release <<OSREL
NAME="${M0_OS_NAME%% [0-9]*}"
VERSION="$M0_OS_VER"
ID=$M0_OS_ID
VERSION_ID="$M0_OS_VER"
PRETTY_NAME="$M0_OS_NAME"
OSREL
        printf '%s \\n \\l\n\n' "$M0_OS_NAME" > /etc/issue 2>/dev/null
        : > /etc/motd 2>/dev/null
        rm -f /etc/alpine-release 2>/dev/null
    fi

    # 6. 藏掉我们自己的启动脚本。/lib/m0 里的东西已经 source 进内存了，
    #    盖一层空 tmpfs 不影响本次运行；重启时挂载没了，init 照样读得到。
    if [ -d /lib/m0 ]; then
        mkdir -p /run/m0/empty
        mount --bind /run/m0/empty /lib/m0 2>/dev/null
    fi
}

m0_install_uname() {
    for d in /bin /usr/bin; do
        [ -e "$d/uname" ] || continue
        rm -f "$d/uname"
        cat > "$d/uname" <<'UNAME'
#!/bin/sh
. /run/m0/persona 2>/dev/null
s=Linux; m=x86_64; p=unknown; i=unknown; o="GNU/Linux"
n=$(cat /proc/sys/kernel/hostname 2>/dev/null)
[ $# -eq 0 ] && { echo "$s"; exit 0; }
out=
for a in "$@"; do
    case "$a" in
        -a|--all)     echo "$s $n $M0_UTS_R $M0_UTS_V $m $o"; exit 0 ;;
        --kernel-name)    out="$out $s" ;;
        --nodename)       out="$out $n" ;;
        --kernel-release) out="$out $M0_UTS_R" ;;
        --kernel-version) out="$out $M0_UTS_V" ;;
        --machine)        out="$out $m" ;;
        --operating-system) out="$out $o" ;;
        --help|--version) echo "Usage: uname [-asnrvmpio]"; exit 0 ;;
        -*) f=${a#-}
            while [ -n "$f" ]; do
                c=${f%"${f#?}"}; f=${f#?}
                case $c in
                    s) out="$out $s" ;; n) out="$out $n" ;;
                    r) out="$out $M0_UTS_R" ;; v) out="$out $M0_UTS_V" ;;
                    m) out="$out $m" ;; p) out="$out $p" ;;
                    i) out="$out $i" ;; o) out="$out $o" ;;
                esac
            done ;;
    esac
done
echo "${out# }"
UNAME
        chmod 0755 "$d/uname"
    done
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
    # 告诉宿主：玩家终端这边的 shell 起来了，现在敲进来的字才有人读。
    # ready 信标只说明 ttyS1 的代理起来了，那比这里早好几秒 —— 自检和截图
    # 在这中间敲命令的话，字会送进一个还没人读的串口，直接丢掉。
    [ -c /dev/ttyS1 ] && echo "{\"ev\":\"console\"}" > /dev/ttyS1
    while true; do
        getty -n -l /bin/sh 115200 ttyS0 xterm-256color
        sleep 1
    done
}
