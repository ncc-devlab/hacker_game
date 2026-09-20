# M0 — 技术脊椎（裸命令行阶段）

M0 的唯一目的：**在引入 Godot 之前，把所有不确定性打掉。**
这一层完全不依赖 Godot、不依赖图形环境，所以可以直接在三端 CI 上跑。

## 阶梯

每一级只比上一级多引入一个变量，故障域因此总是被隔离在最后加进来的那一环。

| 级 | 脚本 | 新引入的变量 | 状态 |
| --- | --- | --- | --- |
| M0-a | `scripts/10-crossover.sh` | 无（`-netdev stream` 直连，零自研代码） | ✅ PASS |
| M0-b | `scripts/20-switched.sh` | 自研二层交换机 | ✅ PASS |
| M0-c | 待做 | Alpine 完整 rootfs + vim/tmux 渲染验证 | ⬜ |
| M0-d | 待做 | Windows / macOS 上复现 a、b | ⬜ |
| M0-e | 待做 | 裁剪构建 QEMU（`--target-list=x86_64-softmmu` 等） | ⬜ |

## 跑起来

```bash
bash m0/scripts/00-fetch-images.sh        # 取 Alpine netboot 内核 + initramfs
bash m0/scripts/01-build-guest-initramfs.sh
bash m0/scripts/10-crossover.sh           # M0-a
bash m0/scripts/20-switched.sh -v         # M0-b，-v 打印每一帧的转发决策
wireshark m0/run/m0.pcap                  # 交换机抓下来的真实流量
```

## 已确认的事实（都是实测/源码核实，不是推测）

**启动 2.2 秒**（TCG，无硬件加速，两台并起）。这对 Apple Silicon 的信心很重要——
M 系列上 x86_64 客户机只能走纯 TCG（HVF 只加速 arm64 客户机），
但这个量级完全可接受，而且还有 `savevm`/`loadvm` 快照这张底牌没打。

**`-netdev stream` 线上格式 = 4 字节大端长度 + 裸以太网帧。**
源码位置：`net/stream_data.c:34` 的 `htonl(size)`、`net/net.c:2100` 的 `net_fill_rstate()` 状态机。
不开 `vnet_hdr` 就没有第二个长度字段。

**所有通道一律走 127.0.0.1 TCP，不用 AF_UNIX。** Windows 版 QEMU 不保证支持
Unix socket，这是唯一三端可移植的选择。`-netdev stream` 的 `addr.type` 支持
`unix` / `inet` / `fd`（见 `qapi/net.json`）。

**方向：宿主先监听，QEMU 当客户端连进来**（`server=off` + `reconnect-ms=1000`）。
踩过的坑：反过来用 `server=on,wait=off` 时，客户机 1.5 秒就启动完并打印完开机信息，
而宿主还没连上，**这些输出被直接丢弃**。宿主先监听则一个字节都不会丢，
而且 VM 重启后 QEMU 会自动接回来。三条通道（终端 / 控制 / 网络）方向因此统一。

**控制通道要等 `ready` 信标再发命令。** QEMU 一启动就把 chardev 连上了（~0.1s），
但客户机要到 ~1.5s 才起 ttyS1 的读取循环，之前发的命令会被丢掉。
命令另外按 2 秒间隔重发——裸串口没有应答层，重发是最省事的可靠化手段。

**终端 resize 只能走控制通道。** 裸串口不是 PTY，没有 `TIOCSWINSZ` 带外信令。
宿主把 `resize <rows> <cols>` 发到 ttyS1，客户机 agent 收到后对 ttyS0 执行 `stty`。
`guest/init` 里已经实现。

**客户机 shell 用 `getty` 起，不用 `sh`。** busybox 没有 `cttyhack` applet，
而 `getty -n -l /bin/sh 115200 ttyS0 xterm-256color` 能给出真正的控制终端
（job control）并设好 `TERM`——这是后面 tmux/vim 正常工作的前提。
外面套 `while true` 循环 respawn：玩家在游戏里敲 `exit` 不能让虚拟机 panic。

## 交换机：为什么自己写

结论是**交换机本体自己写，协议解析绝不自己写**。

开源交换机方案全部出局：VDE2 在 Windows 上没有可用实现（三端发行一票否决）；
Open vSwitch 要 root/内核模块；Linux bridge+TAP 要提权且只有 Linux；
libslirp 只有 NAT 做不了二层互通；`-netdev socket,mcast=` 是集线器不是交换机、
而且没有检测切入点。更根本的是，概要书把交换机定义为流量检测与可视化的
**唯一入口**——用第三方交换机会把这个天然免费的能力变成一个额外的旁路抓包难题。

而「交换机状态」的实际体量就是一张表：

```
mac -> (端口, 最后出现时间)
```

不需要 STP（星型拓扑是我们自己造的，物理上不可能成环）、不需要 VLAN
（等某个关卡真要用再加）、不需要 ARP/IP 逻辑（二层不关心，客户机自己会 ARP）、
不需要分片重组（QEMU 给的就是完整帧）、不需要校验和（客户机内核的事）。
`probe/switch.py` 含注释和 pcap 抓包一共 134 行。

真正会陷进去的是**协议解析**，那里用现成的：**PacketDotNet**（`dotpcap/packetnet`，
纯托管 C#，**MPL-2.0**）。MPL-2.0 是文件级 copyleft——只要不改它自己的源文件，
就能静态链进闭源商业游戏在 Steam 上卖，没有传染。
（对比 SharpPcap 是 GPL 系，而且我们根本不需要它，帧是从 socket 直接来的，不碰 libpcap。）

顺带一个白捡的能力：所有帧都过交换机，写 pcap 只要 24 字节全局头 + 每包 16 字节头，
不需要任何库。调试期能用 Wireshark 看真实流量，后期它直接就是游戏里的抓包道具。

## 不魔改 QEMU（项目硬约束）

QEMU 已经用三条稳定的进程外接口提供了全部所需能力：
**QMP**（控制面：开关机/快照/查状态）、**chardev socket → `-serial`**（数据面：
ttyS0 终端 + ttyS1 隐藏控制通道）、**`-netdev stream`**（网络面：以太网帧）。

将来若需要更深的接入，按这个梯度走，**前三级都不用改一行 QEMU 源码**：

1. 客户机内 agent 经 ttyS1 上报（覆盖全部判定需求，含 inotify/auditd 类监控）
2. QMP `dump-guest-memory` / `pmemsave`，或 `-s` 开 gdbstub 自己写 RSP 客户端
3. **TCG plugin**（`-plugin xxx.so`，指令/访存级插桩）——这是不 fork 的正式逃生舱。
   注意 `include/plugins/qemu-plugin.h` 在 11.1.1 里是 `GPL-2.0-or-later`，
   插件本身要 GPL 兼容，但它是独立文件，跟游戏本体互不沾染。
4. 自定义虚拟设备 / 改 TCG 语义 —— 才需要碰源码。目前看不到这种需求。

代价方面：现在 Godot 和 QEMU 是两个进程走 socket，属于聚合，游戏代码不受 GPL 传染；
**一旦 patch 源码就有了发布修改版源码的义务**，且要永久跟着 Steam 发行走，
再叠加三端 × 每次上游安全更新的维护成本。

需要自己构建 QEMU 是另一回事，那叫**裁剪不叫魔改**：
`--target-list=x86_64-softmmu --without-default-features --disable-gtk --disable-sdl
--disable-curses --disable-vnc --disable-slirp`，目标是砍体积和杀软误报面积。
