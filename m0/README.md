# M0 — 技术脊椎（裸命令行阶段）

M0 的唯一目的：**在引入 Godot 之前，把所有不确定性打掉。**
这一层完全不依赖 Godot、不依赖图形环境，所以可以直接在三端 CI 上跑。

## 阶梯

每一级只比上一级多引入一个变量，故障域因此总是被隔离在最后加进来的那一环。

| 级 | 脚本 | 新引入的变量 | 状态 |
| --- | --- | --- | --- |
| M0-a | `scripts/10-crossover.sh` | 无（`-netdev stream` 直连，零自研代码） | ✅ PASS |
| M0-b | `scripts/20-switched.sh` | 自研二层交换机 | ✅ PASS |
| M0-c | `scripts/30-terminal.sh` | Alpine 完整 rootfs + vim/tmux 渲染验证 | ✅ PASS (11/11) |
| M0-d | 待做 | Windows / macOS 上复现 a、b、c | ⬜ |
| M1 | `dotnet test` | Core 翻成 C#，三端 CI 可跑 | 🔨 进行中（14 项单测绿） |
| M0-e | `scripts/40-build-qemu.sh` | 裁剪构建 QEMU | ✅ PASS |

## 跑起来

```bash
bash m0/scripts/00-fetch-images.sh          # 取 Alpine netboot 内核 + initramfs
bash m0/scripts/02-build-alpine-rootfs.sh   # 造主角机 qcow2（vim/tmux）
bash m0/scripts/01-build-guest-initramfs.sh # 烤目标机 initramfs（含 ext4）
bash m0/scripts/10-crossover.sh             # M0-a 交叉直连
bash m0/scripts/20-switched.sh -v           # M0-b 自研交换机，-v 打印每帧转发决策
bash m0/scripts/30-terminal.sh              # M0-c 终端渲染验收
bash m0/scripts/40-build-qemu.sh            # M0-e 裁剪构建 QEMU
wireshark m0/run/m0.pcap                    # 交换机抓下来的真实流量
```

02 要在 01 之前跑：01 需要从 apk 取 ext4 模块，而那套 apk 调用在 `lib.sh` 里，
两者共用同一份 minirootfs 缓存。

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

## M0-c 的发现（终端渲染）

验证方式不是肉眼看，而是在宿主上跑真正的 xterm 兼容模拟器（pyte）渲染
ttyS0 的原始字节流，再对渲染结果做断言（`probe/term.py`）。
这把两个风险解耦了：**字节流对不对**是我们的责任，**godot-xterm 渲染对不对**
是它的责任。本层绿了之后，godot-xterm 里的任何不一致都能直接定位到它身上。

**三条 pyte 处理不了、但需要终端实现的序列**：`CSI 6 n`（DSR 光标位置查询）、
`CSI > 4 ; 2 m`（XTMODKEYS）、`ESC ( 0`（DEC 特殊图形，tmux 用它画框线）。

> ⚠ 更正：这三条我一度记成「必须交给 godot-xterm 验证的风险」。
> 实测下来**三条都是 pyte 自己的缺陷，godot-xterm 的引擎（libtsm）全部正确处理**。
> 证据与选型结论见 [终端引擎选型](../docs/终端引擎选型.md)。

**客户机必须挂 devpts，否则 tmux 起不来。** devtmpfs 不会自动建 `/dev/pts` 目录，
不先 `mkdir` 的话 `mount -t devpts` 会失败，症状是 tmux 报
`create window failed: fork failed: No such file or directory`。
`guest/common.sh` 的 `m0_mount_pty()` 负责这件事，两种客户机都调用。

**主角机镜像全程不用 root 构建**：apk 通过 musl loader 直接在宿主上跑
（`--root` 指向目标目录），`mke2fs -d` 直接从目录生成 ext4 不用 mount，
`fakeroot` 保证文件属主落成 0:0。产物 21MB qcow2，含 Alpine 3.23.6 + vim 9.2 + tmux 3.6，
**2.8 秒启动**（TCG，无硬件加速）。

**测试哨兵必须每条命令唯一。** 用固定字样会出一个隐蔽的竞态：哨兵一旦出现就
永久留在流水里，下一次等待立刻命中、根本没等新输出，取回的还是上一条命令的区间。
这个 bug 表现为结果在多次运行之间飘忽不定。

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

## M0-e 的发现（裁剪构建）

**先说对照口径。** 拿发行版 QEMU 当基准是错的——那份我们**永远发不了**：
Windows 上 MSYS2 的构建绑死在 MSYS 路径且自带 GPL 依赖，
macOS 上 homebrew 的构建链接 `/opt/homebrew` 不可重定位、还得自己签名公证
并加 JIT entitlement。**三端都必须自建**，所以真正该比的是
「自建裁剪」对「自建不裁剪」。

拆开看，两部分收益的性质完全不同：

| 动作 | 效果 | 是否与构建机有关 |
| --- | --- | --- |
| **裁剪 + strip 安装产物** | 342M → **29M** | 无关，纯后处理 |
| **`--without-default-features`** | 二进制 27M vs 28M，几乎不变 | **强相关，这才是重点** |

体积那部分几乎全来自后处理（固件白名单 + strip + 删掉用不到的二进制），
跟 configure 开关没什么关系。

**`--without-default-features` 真正买到的是确定性。** 不加它的话，
链进去什么完全取决于构建机上恰好装了哪些 dev 包。在这台精简机器上
试算只多出 capstone / libudev / libusb 三个；而 Arch 自己的包证明
在一台"胖"镜像上同样的 configure 会得到 **63 个 .so**。
三端各自的 CI 镜像不可能一致，**构建不可复现**比多 13MB 严重得多。

裁剪版稳定只链 8 个：pixman、zlib、libfdt、glib、pcre2、libm、libc、ld。
杀软误报面也随之消失——整个加密栈、fuse、rdma、bpf、jpeg 都不在了。

**三个踩过的坑：**

1. **`--enable-zlib` 不存在。** QEMU 把 zlib 当硬依赖，由 pkg-config 找，不是可选特性。
2. **不能加 `--disable-install-blobs`。** 即便用 `-kernel` 直接引导内核，
   q35 仍然要 SeaBIOS 来完成装载，缺了会报
   `could not load PC BIOS 'bios-256k.bin'`。
3. **固件必须用白名单而不是黑名单。** `install` 会装上全部架构的固件，
   光 edk2 的 arm/aarch64/riscv/loongarch UEFI 镜像就 **313MB**，
   而我们只跑 x86_64、且完全不碰 UEFI。白名单只留三个文件：
   `bios-256k.bin`、`kvmvapic.bin`、`linuxboot_dma.bin`。

**`virtio-net-pci` 要带 `romfile=`。** 它默认加载 `efi-virtio.rom` 这个 PXE 引导 ROM，
我们永远不网络引导，关掉能少发一个文件、少一段塞进客户机内存的代码。
不关的话裁剪掉那个 rom 就会启动失败。（同理，不显式指定网卡时 q35 会造一个
默认 e1000e 并去找 `efi-e1000e.rom`，所以裸跑 QEMU 调试时记得带 `-nic none`。）

**白名单是会踩人的，所以它必须跟回归套件绑在一起。** 我就踩了一次：
概要书里开局那台机器是 **FreeDOS，它需要 VGA 文本模式而不是串口**，
而白名单最初没有 `vgabios-stdvga.bin`，`-vga std` 直接起不来。已补上（4KB）。

**好消息是 FreeDOS 那条线不需要把图形后端加回来。** 实测裁剪版
（无 VNC / 无 SDL / 无 GTK）配 `-vga std` + QMP `screendump`
能拿到 720×400 的 PPM，而 PPM（P6）是裸 RGB，Godot 解起来很容易。
也就是说客户机画面进 Godot 这条路，靠 QMP 就够了。

裁剪版已回归 M0-b 与 M0-c，全绿。

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
