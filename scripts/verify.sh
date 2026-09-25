#!/bin/bash
# macOS / Linux 端到端验证：确认这台机器能不能把游戏的技术脊椎跑起来，并留下完整日志。
# （Windows 用 scripts/verify-windows.ps1；scripts/verify-macos.sh 是本脚本的别名。）
#
# 与 scripts/verify-windows.ps1 一一对应：
#   1. 环境信息            5. Core 测试（dotnet test，含真实起 QEMU 的集成测试）
#   2. .NET SDK            6. Godot：mono 版、GDExtension、C# 编译、导入、无头自检
#   3. QEMU 及伪装所需设备   7. 退出后有没有残留的 qemu 进程
#   4. 客户机镜像
# 所有输出落到 m0/run/verify-<平台>-<时间戳>/，最后打成同名 zip，把 zip 发回来即可。
#
# 用法：
#   scripts/verify.sh                 只检查、不装任何东西
#   scripts/verify.sh --install       缺什么装什么（不需要 sudo）：
#                                       .NET 10 SDK -> ~/.dotnet
#                                       QEMU        -> brew install qemu（仅 macOS；Linux 请用包管理器）
#                                       Godot mono  -> macOS: ~/Applications/Godot_mono.app
#                                                      Linux: ~/.local/share/godot-mono/
#                                       godot-xterm -> 项目 addons/
#   scripts/verify.sh --godot /path/to/Godot_mono.app --qemu /path/to/qemu-system-x86_64
#   scripts/verify.sh --offline       不联网（插件缺失时直接报错）
#
# 客户机镜像只能在 Linux 上构建（m0/scripts/）。其他平台在 Linux 上跑
# scripts/pack-for-windows.sh，把产出的 zip 解到仓库根即可（包内容与平台无关，名字是历史原因）。
#
# 注意：刻意只用 macOS 自带的 bash 3.2 就有的语法（不用关联数组、mapfile、${x,,}），
# 也不依赖 coreutils —— macOS 没有 timeout 命令，超时用 perl 的 alarm 做。

set -o pipefail

GODOT_VERSION="4.7.2"
XTERM_VERSION="v4.0.3"
BOOT_TIMEOUT=180
INSTALL=0
OFFLINE=0
GODOT_ARG=""
QEMU_ARG=""
GODOT_ENV="${GODOT:-}"     # 下面 GODOT 会被用作结果变量，先把环境变量里的留一份

while [ $# -gt 0 ]; do
    case "$1" in
        --install) INSTALL=1 ;;
        --offline) OFFLINE=1 ;;
        --godot)   GODOT_ARG="$2"; shift ;;
        --qemu)    QEMU_ARG="$2"; shift ;;
        --boot-timeout) BOOT_TIMEOUT="$2"; shift ;;
        -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
        *) echo "未知参数: $1" >&2; exit 2 ;;
    esac
    shift
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname -s)" in
    Darwin) PLATFORM=macos ;;
    Linux)  PLATFORM=linux ;;
    *) echo "不支持的系统: $(uname -s)（Windows 用 scripts/verify-windows.ps1）" >&2; exit 2 ;;
esac
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO/m0/run/verify-$PLATFORM-$STAMP"
mkdir -p "$OUT"
# 全部控制台输出同时进 transcript.txt
exec > >(tee "$OUT/transcript.txt") 2>&1

REPORT="$OUT/report.txt"
: > "$REPORT"
FAILS=0
TOTAL=0

step() {   # step <0|1> <名称> [说明]
    TOTAL=$((TOTAL + 1))
    if [ "$1" = 1 ]; then
        printf '\033[32m[PASS]\033[0m %s  %s\n' "$2" "${3:-}"
        printf 'PASS\t%s\t%s\n' "$2" "${3:-}" >> "$REPORT"
    else
        FAILS=$((FAILS + 1))
        printf '\033[31m[FAIL]\033[0m %s  %s\n' "$2" "${3:-}"
        printf 'FAIL\t%s\t%s\n' "$2" "${3:-}" >> "$REPORT"
    fi
}
section() { printf '\n\033[36m=== %s ===\033[0m\n' "$1"; }
ok() { if "$@" >/dev/null 2>&1; then echo 1; else echo 0; fi; }

# run_logged <日志名> <超时秒> <失败模式|""> <命令...>
# stdout/stderr 各落一个文件，结果放进 $RC：
#   退出码；超时 -1；stderr 命中失败模式提前终止 -3
# 失败模式存在的理由：C# 没编译出来时 Godot 找不到 Main 类，场景里没有任何
# 代码会调 Quit，只会空转到超时 —— Windows 第一轮验证就白等了 360 秒。
# 轮询过程中顺带记下 stdout 首次出现 $WATCH 的时刻（秒），放进 $WATCH_AT。
run_logged() {
    local name="$1" limit="$2" pattern="$3"; shift 3
    local so="$OUT/$name.out.txt" se="$OUT/$name.err.txt"
    local start now pid
    WATCH_AT=""
    "$@" > "$so" 2> "$se" &
    pid=$!
    start=$(perl -MTime::HiRes=time -e 'print time')
    while kill -0 "$pid" 2>/dev/null; do
        now=$(perl -MTime::HiRes=time -e 'print time')
        if [ -n "${WATCH:-}" ] && [ -z "$WATCH_AT" ] && grep -q "$WATCH" "$so" 2>/dev/null; then
            WATCH_AT=$(perl -e "printf '%.1f', $now - $start")
        fi
        if perl -e "exit(!($now - $start > $limit))"; then
            kill "$pid" 2>/dev/null; sleep 1; kill -9 "$pid" 2>/dev/null
            wait "$pid" 2>/dev/null; RC=-1; return
        fi
        if [ -n "$pattern" ] && grep -qE "$pattern" "$se" 2>/dev/null; then
            kill "$pid" 2>/dev/null; sleep 1; kill -9 "$pid" 2>/dev/null
            wait "$pid" 2>/dev/null; RC=-3; return
        fi
        sleep 0.5
    done
    wait "$pid"; RC=$?
}

# ---------------------------------------------------------------------------
section '1. 环境'
ARCH="$(uname -m)"
{
    if [ "$PLATFORM" = macos ]; then
        echo "系统:  $(sw_vers -productName) $(sw_vers -productVersion) ($(sw_vers -buildVersion))"
        echo "架构:  $ARCH"
        echo "CPU:   $(sysctl -n machdep.cpu.brand_string)  逻辑核 $(sysctl -n hw.logicalcpu)"
        echo "内存:  $(( $(sysctl -n hw.memsize) / 1073741824 )) GB"
    else
        echo "系统:  $( (. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") || echo Linux)  内核 $(uname -r)"
        echo "架构:  $ARCH"
        echo "CPU:   $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | sed 's/^ *//')  逻辑核 $(nproc)"
        echo "内存:  $(( $(awk '/MemTotal/ {print $2}' /proc/meminfo) / 1048576 )) GB"
    fi
    echo "bash:  $BASH_VERSION"
    echo "仓库:  $REPO"
} | tee "$OUT/environment.txt"
step 1 '环境信息已记录'
if [ "$ARCH" = arm64 ] || [ "$ARCH" = aarch64 ]; then
    echo '  注意：ARM 主机上 x86_64 客户机没有硬件加速，纯 TCG'
fi

# ---------------------------------------------------------------------------
section '2. .NET SDK'
# dotnet-install.sh 默认装到 ~/.dotnet，官方 pkg 装到 /usr/local/share/dotnet，
# 两处都不一定在 PATH 上（尤其是 ssh 进来的非登录 shell）；Linux 发行版的包在 /usr/{share,lib}/dotnet
DOTNET=""
find_dotnet() {
    local c
    for c in "$(command -v dotnet 2>/dev/null)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet \
             /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet; do
        [ -n "$c" ] && [ -x "$c" ] || continue
        if "$c" --list-sdks 2>/dev/null | grep -qE '^(8|9|[1-9][0-9])\.'; then DOTNET="$c"; return; fi
    done
}
find_dotnet
if [ -z "$DOTNET" ] && [ "$INSTALL" = 1 ] && [ "$OFFLINE" = 0 ]; then
    echo '  安装 .NET 10 SDK 到 ~/.dotnet …'
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$OUT/dotnet-install.sh" \
        && bash "$OUT/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet" > "$OUT/dotnet-install.log" 2>&1
    find_dotnet
fi
SEEN=""
for c in "$(command -v dotnet 2>/dev/null)" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet \
         /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet; do
    [ -n "$c" ] && [ -x "$c" ] || continue
    # PATH 里的那份往往就是下面两处之一，按真实路径去重
    real="$(cd "$(dirname "$c")" && pwd -P)/$(basename "$c")"
    case " $SEEN " in *" $real "*) continue ;; esac
    SEEN="$SEEN $real"
    echo "$c"
    echo "  SDK:    $("$c" --list-sdks 2>/dev/null | tr '\n' ';')"
    echo "  运行时: $("$c" --list-runtimes 2>/dev/null | tr '\n' ';')"
done > "$OUT/dotnet-info.txt"
cat "$OUT/dotnet-info.txt"
if [ -n "$DOTNET" ]; then
    step 1 '.NET SDK' "$DOTNET"
    export PATH="$(dirname "$DOTNET"):$PATH"
    # Godot 靠它找到 ~/.dotnet 里的运行时。PATH 上那份可能是软链接（Linux 的 /usr/bin/dotnet），取真实目录
    DOTNET_REAL="$DOTNET"
    while [ -L "$DOTNET_REAL" ]; do
        LINK="$(readlink "$DOTNET_REAL")"
        case "$LINK" in /*) DOTNET_REAL="$LINK" ;; *) DOTNET_REAL="$(dirname "$DOTNET_REAL")/$LINK" ;; esac
    done
    export DOTNET_ROOT="$(cd "$(dirname "$DOTNET_REAL")" && pwd -P)"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
else
    step 0 '.NET SDK' '没有 8 或更新版本的 SDK（只有运行时不够）。加 --install 自动装，或装 .NET 10 SDK'
fi

# ---------------------------------------------------------------------------
section '3. QEMU'
QEMU=""
# 与 QemuLocator.DescribePlatform 一致
case "$PLATFORM-$ARCH" in
    macos-arm64) QEMU_PLATFORM_DIR=macos-arm64 ;;
    macos-*)     QEMU_PLATFORM_DIR=macos-x86_64 ;;
    *)           QEMU_PLATFORM_DIR=linux-x86_64 ;;
esac
for c in "$QEMU_ARG" "${GAMEHACKER_QEMU:-}" \
         "$REPO/runtime/$QEMU_PLATFORM_DIR/bin/qemu-system-x86_64" \
         /opt/homebrew/bin/qemu-system-x86_64 /usr/local/bin/qemu-system-x86_64 \
         /opt/local/bin/qemu-system-x86_64 "$(command -v qemu-system-x86_64 2>/dev/null)"; do
    if [ -n "$c" ] && [ -x "$c" ]; then QEMU="$c"; break; fi
done
if [ -z "$QEMU" ] && [ "$INSTALL" = 1 ] && [ "$OFFLINE" = 0 ] && command -v brew >/dev/null; then
    echo '  brew install qemu（约 700MB，要几分钟）…'
    brew install qemu > "$OUT/brew-qemu.log" 2>&1
    for c in /opt/homebrew/bin/qemu-system-x86_64 /usr/local/bin/qemu-system-x86_64; do
        [ -x "$c" ] && QEMU="$c" && break
    done
fi
if [ -z "$QEMU" ]; then
    if [ "$PLATFORM" = macos ]; then
        step 0 'QEMU 可执行文件' '找不到。加 --install（需要 Homebrew），或 --qemu 指定'
    else
        step 0 'QEMU 可执行文件' '找不到。用包管理器装（Arch / Debian / Ubuntu 都叫 qemu-system-x86），或 --qemu 指定'
    fi
else
    # 只有 --qemu 显式指定时才交给游戏。其余情况让游戏自己找（QemuLocator 会看
    # /opt/homebrew/bin、C:\Program Files\qemu 等位置），这样自检顺带验证了从 Finder 启动也能跑
    [ -n "$QEMU_ARG" ] && export GAMEHACKER_QEMU="$QEMU"
    VER="$("$QEMU" -version 2>&1 | head -1)"
    step "$(ok "$QEMU" -version)" 'QEMU 可执行文件' "$QEMU  ($VER)"
    CPUS="$("$QEMU" -cpu help 2>&1)"; DEVS="$("$QEMU" -device help 2>&1)"; ACCEL="$("$QEMU" -accel help 2>&1)"
    printf '%s\n\n%s\n\n%s\n\n%s\n' "$VER" "$ACCEL" "$CPUS" "$DEVS" > "$OUT/qemu-capabilities.txt"
    step "$(ok sh -c 'echo "$1" | grep -qw IvyBridge && echo "$1" | grep -qw Westmere' _ "$CPUS")" \
         'QEMU 支持 -cpu IvyBridge / Westmere'
    step "$(ok sh -c 'echo "$1" | grep -q "\"e1000e\""' _ "$DEVS")" 'QEMU 支持 e1000e 网卡'
    step "$(ok sh -c 'echo "$1" | grep -q "\"ich9-ahci\"" && echo "$1" | grep -q "\"ide-hd\""' _ "$DEVS")" \
         'QEMU 支持 ich9-ahci / ide-hd'
    echo "  加速器: $(echo "$ACCEL" | tail -n +2 | tr '\n' ' ')"
fi

# ---------------------------------------------------------------------------
section '4. 客户机镜像'
MISSING=""
for f in vmlinuz-virt m0-guest.cpio.gz alpine-main.qcow2; do
    [ -f "$REPO/m0/images/$f" ] || MISSING="$MISSING $f"
done
if [ -n "$MISSING" ]; then
    step 0 '客户机镜像' "缺少$MISSING。镜像只能在 Linux 上构建：Linux 上跑 scripts/pack-for-windows.sh，把 zip 解到仓库根"
else
    # 不用 ls -l 取列：中文 locale 下日期占的列数不同（"9月22日" 是一列）
    SIZES=""
    for f in vmlinuz-virt m0-guest.cpio.gz alpine-main.qcow2; do
        SIZES="$SIZES$(wc -c < "$REPO/m0/images/$f" | awk -v n="$f" '{printf "%s=%.1fMB ", n, $1/1048576}')"
    done
    step 1 '客户机镜像' "$SIZES"
fi

# ---------------------------------------------------------------------------
section '5. Core 测试（含真实 QEMU 集成测试）'
if [ -n "$DOTNET" ]; then
    run_logged dotnet-test 900 "" dotnet test "$REPO/GameHacker.slnx" --nologo -v q
    SUMMARY="$(grep -E '通过|Passed|失败|Failed' "$OUT/dotnet-test.out.txt" | tail -1 | sed 's/^ *//')"
    step "$([ "$RC" = 0 ] && echo 1 || echo 0)" 'dotnet test' "${SUMMARY:-退出码 $RC}"
    # 缺镜像时集成测试会跳过而不是失败，"通过"不代表真的起过虚拟机
    if echo "$SUMMARY" | grep -qE '已跳过: *[1-9]|Skipped: *[1-9]'; then
        echo '  注意：有测试被跳过（通常是缺镜像），上面的通过不代表真的起过虚拟机'
    fi
else
    step 0 'dotnet test' '跳过：没有 .NET SDK（见第 2 步）'
fi

# ---------------------------------------------------------------------------
section '6. Godot'
PROJECT="$REPO/src/GameHacker.Godot"
GODOT=""
resolve_godot() {
    local c app
    for c in "$GODOT_ARG" "$GODOT_ENV" /Applications/Godot_mono.app "$HOME/Applications/Godot_mono.app" \
             "$HOME/gh-setup/Godot_mono.app" "$HOME/.local/share/godot-mono/Godot_v${GODOT_VERSION}-stable_mono_linux.x86_64" \
             "$(command -v godot-mono 2>/dev/null)" "$(command -v godot 2>/dev/null)"; do
        [ -n "$c" ] || continue
        # 给 .app 的话取里面真正的可执行文件
        case "$c" in *.app|*.app/) app="${c%/}/Contents/MacOS/Godot"; c="$app" ;; esac
        [ -x "$c" ] && { GODOT="$c"; return; }
    done
}
resolve_godot
if [ -z "$GODOT" ] && [ "$INSTALL" = 1 ] && [ "$OFFLINE" = 0 ]; then
    echo "  下载 Godot $GODOT_VERSION .NET（约 190MB）…"
    GURL="https://github.com/godotengine/godot/releases/download/$GODOT_VERSION-stable"
    if [ "$PLATFORM" = macos ]; then
        mkdir -p "$HOME/Applications"
        curl -fsSL -o "$OUT/godot.zip" "$GURL/Godot_v$GODOT_VERSION-stable_mono_macos.universal.zip" \
            && unzip -q -o "$OUT/godot.zip" -d "$HOME/Applications" && rm -f "$OUT/godot.zip"
    else
        # zip 里是一个同名目录，可执行文件和 GodotSharp/ 必须并排放着
        GDIR="Godot_v$GODOT_VERSION-stable_mono_linux_x86_64"
        curl -fsSL -o "$OUT/godot.zip" "$GURL/$GDIR.zip" \
            && mkdir -p "$HOME/.local/share" && rm -rf "$HOME/.local/share/godot-mono" \
            && unzip -q -o "$OUT/godot.zip" -d "$HOME/.local/share" \
            && mv "$HOME/.local/share/$GDIR" "$HOME/.local/share/godot-mono" \
            && rm -f "$OUT/godot.zip"
    fi
    resolve_godot
fi

if [ -z "$GODOT" ]; then
    step 0 'Godot mono' "找不到。加 --install，或 --godot 指向 Godot .NET 版（macOS 可以直接给 .app）"
else
    GV="$("$GODOT" --version 2>/dev/null | tail -1)"
    # 非 mono 版能打开工程但加载不了任何 .cs 脚本，错误信息很迷惑，这里先拦住
    step "$(echo "$GV" | grep -q mono && echo 1 || echo 0)" 'Godot mono' "$GODOT  ($GV)"

    # 浏览器下载的 .app / 仓库 zip 会带 quarantine 属性：Godot 本体会被 Gatekeeper 拦，
    # GDExtension 的 .framework 会静悄悄加载失败（Terminal 节点类型不存在）
    APP_DIR="${GODOT%/Contents/MacOS/Godot}"
    if [ "$PLATFORM" = macos ] && xattr -p com.apple.quarantine "$APP_DIR" >/dev/null 2>&1; then
        echo "  注意：$APP_DIR 带 quarantine 属性，Gatekeeper 可能拦截。解除：xattr -dr com.apple.quarantine \"$APP_DIR\""
    fi

    # --- 插件 ---
    ADDON="$PROJECT/addons/godot_xterm"
    if [ ! -d "$ADDON" ]; then
        if [ "$OFFLINE" = 1 ]; then
            echo '  插件缺失且处于 --offline'
        else
            echo "  下载 godot-xterm $XTERM_VERSION …"
            TMPX="$OUT/xterm-extract"
            curl -fsSL -o "$OUT/xterm.zip" \
                "https://github.com/lihop/godot-xterm/releases/download/$XTERM_VERSION/godot-xterm-$XTERM_VERSION.zip" \
                && unzip -q -o "$OUT/xterm.zip" -d "$TMPX" \
                && mkdir -p "$PROJECT/addons" \
                && cp -R "$(find "$TMPX" -maxdepth 3 -type d -name godot_xterm | head -1)" "$PROJECT/addons/"
            rm -rf "$TMPX" "$OUT/xterm.zip"
        fi
    fi
    if [ "$PLATFORM" = macos ]; then
        FW="$ADDON/lib/libgodot-xterm.macos.template_debug.framework"
        step "$([ -d "$FW" ] && echo 1 || echo 0)" 'godot-xterm 插件（macOS GDExtension）' "$FW"
    else
        FW="$ADDON/lib/libgodot-xterm.linux.template_debug.x86_64.so"
        step "$([ -f "$FW" ] && echo 1 || echo 0)" 'godot-xterm 插件（Linux x86_64 GDExtension）' "$FW"
    fi
    if [ "$PLATFORM" = macos ] && [ -d "$ADDON" ] && xattr -r "$ADDON" 2>/dev/null | grep -q com.apple.quarantine; then
        if [ "$INSTALL" = 1 ]; then
            xattr -dr com.apple.quarantine "$ADDON" && echo '  已解除插件的 quarantine 属性'
        else
            echo "  注意：插件带 quarantine 属性，GDExtension 可能加载失败。解除：xattr -dr com.apple.quarantine \"$ADDON\""
        fi
    fi

    # --- 编译 C# 并导入资源 ---
    # 第一次打开工程必须先 --import，否则 GDExtension 还没被扫描，Terminal 节点类型不存在
    if [ -n "$DOTNET" ]; then
        run_logged godot-build 300 "" dotnet build "$PROJECT/GameHacker.Godot.csproj" --nologo -v q
        step "$([ "$RC" = 0 ] && echo 1 || echo 0)" 'Godot 工程 C# 编译' "$(tail -2 "$OUT/godot-build.out.txt" | tr '\n' ' ')"
    else
        step 0 'Godot 工程 C# 编译' '跳过：没有 .NET SDK（见第 2 步）'
    fi
    # 没有 .NET 时 Godot mono 连 --import 都会卡住（报 hostfxr 找不到然后不退出），
    # 实测会白等满 300 秒，所以直接跳过；有 SDK 时也挂上同样的失败模式兜底
    if [ -z "$DOTNET" ]; then
        step 0 'Godot 资源导入' '跳过：没有 .NET SDK，Godot mono 找不到 .NET 运行时会卡住'
    else
        run_logged godot-import 300 'Failed to load .NET runtime|hostfxr' \
            "$GODOT" --headless --path "$PROJECT" --import
        if [ "$RC" = -3 ]; then
            step 0 'Godot 资源导入' 'Godot 找不到 .NET 运行时（hostfxr）。检查 DOTNET_ROOT'
        else
            step "$([ "$RC" = 0 ] && echo 1 || echo 0)" 'Godot 资源导入' "退出码 $RC"
        fi
    fi

    # --- 自检 ---
    if [ -z "$DOTNET" ]; then
        step 0 'Godot 自检' '跳过：没有 .NET SDK，C# 脚本编译不出来（见第 2 步）'
    else
        export GAMEHACKER_BOOT_TIMEOUT="$BOOT_TIMEOUT"
        export GAMEHACKER_SELFTEST="$OUT/selftest.txt"
        export GAMEHACKER_PCAP="$OUT/capture.pcap"
        # 顺带验玩家的真实路径：鼠标点击聚焦终端后，Tab 补全与方向键都要能用
        export GAMEHACKER_PROBE_CLICK=1
        WATCH='台机器就绪'
        T0=$(date +%s)
        run_logged godot-selftest $((BOOT_TIMEOUT + 180)) \
            'Cannot instantiate C# script|Failed to load .NET runtime|hostfxr' \
            "$GODOT" --headless --path "$PROJECT"
        WATCH=""
        ELAPSED=$(( $(date +%s) - T0 ))
        if [ -f "$GAMEHACKER_SELFTEST" ]; then
            N=$(grep -c . "$GAMEHACKER_SELFTEST"); P=$(grep -c '^PASS' "$GAMEHACKER_SELFTEST")
            step "$([ "$RC" = 0 ] && [ "$N" = "$P" ] && echo 1 || echo 0)" "Godot 自检 (${ELAPSED}s)" \
                 "$P/$N 项通过，退出码 $RC"
            sed 's/^/    /' "$GAMEHACKER_SELFTEST"
        else
            case "$RC" in
                -3) WHY='Godot 加载不了 C# 脚本（C# 没编译出来，或 .NET 运行时加载失败——检查 DOTNET_ROOT）' ;;
                -1) WHY='超时' ;;
                *)  WHY="退出码 $RC" ;;
            esac
            step 0 "Godot 自检 (${ELAPSED}s)" "没有生成报告：$WHY。见 godot-selftest.*.txt"
            tail -20 "$OUT/godot-selftest.err.txt"
        fi
        # Apple Silicon 上 x86_64 客户机只有纯 TCG，启动耗时是三端承诺的直接风险，单独记一笔
        [ -n "$WATCH_AT" ] && echo "  两台虚拟机就绪用时: ${WATCH_AT}s（进程启动起算，含 Godot 自身启动）" \
            | tee -a "$OUT/environment.txt"
        unset GAMEHACKER_SELFTEST GAMEHACKER_PCAP GAMEHACKER_PROBE_CLICK
    fi

    # Godot 自己的日志（user://logs）一并带走
    if [ "$PLATFORM" = macos ]; then
        UL="$HOME/Library/Application Support/Godot/app_userdata/GameHacker/logs"
    else
        UL="${XDG_DATA_HOME:-$HOME/.local/share}/godot/app_userdata/GameHacker/logs"
    fi
    [ -d "$UL" ] && cp -R "$UL" "$OUT/godot-user-logs" 2>/dev/null
fi

# ---------------------------------------------------------------------------
section '7. 残留进程'
sleep 2
# 只认本游戏拉起的 qemu（命令行里带我们的 initramfs）；开发机上别的虚拟机不算、也不杀
OURS='qemu-system.*m0-guest\.cpio\.gz'
ORPHANS="$(pgrep -f "$OURS" | wc -l | tr -d ' ')"
# TCG 每台占满一核，孤儿会让下一次启动越跑越慢直至超时 —— M2 里在 Linux 上实测到过
step "$([ "$ORPHANS" = 0 ] && echo 1 || echo 0)" '退出后没有残留 qemu' "$ORPHANS 个"
if [ "$ORPHANS" != 0 ]; then
    pgrep -fl "$OURS"
    pkill -f "$OURS"
fi

# ---------------------------------------------------------------------------
section '汇总'
if [ "$FAILS" = 0 ]; then
    printf '\033[32m全部 %d 项通过\033[0m\n' "$TOTAL"
else
    printf '\033[31m%d/%d 项未通过：\033[0m\n' "$FAILS" "$TOTAL"
    grep '^FAIL' "$REPORT" | cut -f2- | sed 's/\t/: /; s/^/  - /'
fi

echo
echo "日志目录: $OUT"
echo "打包:     $OUT.zip   <- 把这个发回来"
# zip 放在 tee 之后的最后一步；transcript 在 exec 重定向结束前可能还差最后几行，无妨
(cd "$(dirname "$OUT")" && zip -qr "$OUT.zip" "$(basename "$OUT")")

[ "$FAILS" = 0 ]
