# M1 — GameHacker.Core

## 为什么 Core 不依赖 Godot

概要书要求 Windows / macOS / Linux 三端稳定重现。这件事必须能在 CI 上自动验证，
而不是靠手工开三台机器点。

`GameHacker.Core` 是纯 .NET 类库，**零 Godot 依赖**。因此三端 runner 上一句
`dotnet test` 就能跑完整条技术脊椎——起两台真实虚拟机、过自研交换机、
ping 通、判定触发——不需要 Godot、不需要图形环境。
Godot 项目只是引用它的一层薄壳。

## 结构

| 文件 | 对应的 M0 原型 | 职责 |
| --- | --- | --- |
| `Net/StreamFrameCodec.cs` | `probe/switch.py` 的拆帧部分 | QEMU `-netdev stream` 的 4 字节大端成帧 |
| `Net/MacAddressKey.cs` | — | MAC 的值类型封装，转发热路径上无分配 |
| `Net/EthernetFrame.cs` | — | 以太网帧头的最小视图 |
| `Net/VirtualSwitch.cs` | `probe/switch.py` | 用户态二层学习交换机 |
| `Qemu/QmpClient.cs` | — | QMP 控制面（开关机 / 快照 / 查状态） |
| `Qemu/QemuLauncher.cs` | `probe/m0lib.py` 的 `Vm` | 按 VmSpec 拉起 QEMU 并接好三条通道 |
| `Channels/SerialChannel.cs` | `probe/m0lib.py` 的 `Listener` | 一条串口通道的宿主端 |
| `Channels/ControlChannel.cs` | `m0lib` 的 `ctl_request` | ttyS1 隐藏控制通道的请求/应答 |

## 测试分两层

**单元测试**锁死的是 QEMU 的线上格式，不是我们的设计自由度。
`StreamFrameCodecTests` 里的断言直接对应 `net/stream_data.c:34` 与 `net/net.c:2100`。
QEMU 升级后若它们挂了，说明上游改了格式，要去读源码而不是改断言。

**集成测试**（`SwitchIntegrationTests`）真的起虚拟机。它是
`m0/probe/run_switched.py` 的 C# 对应物，两边必须得出同样的结论。
镜像是不入库的构建产物，所以缺镜像时用 `SkippableFact` **跳过**而不是失败
——失败会把「环境没准备好」和「代码坏了」混为一谈。

反向验证过：把 `GAMEHACKER_QEMU` 指向一个不会启动虚拟机的程序，
集成测试确实会失败而不是空跑通过。

## 跑起来

```bash
# 先备好 M0 镜像
bash m0/scripts/00-fetch-images.sh
bash m0/scripts/02-build-alpine-rootfs.sh
bash m0/scripts/01-build-guest-initramfs.sh

dotnet test GameHacker.slnx

# CI 上用环境变量指向本平台的裁剪版 QEMU
GAMEHACKER_QEMU=/path/to/qemu-system-x86_64 dotnet test GameHacker.slnx
```

## 两条必须遵守的约定

**目标框架停在 net8.0。** 那是 Godot 4 的 C# 运行时版本。开发机上未必装了
.NET 8 运行时，所以测试工程用 `<RollForward>LatestMajor</RollForward>`
在更高版本上跑，被测代码本身仍按 net8.0 编译。

**所有 socket 一律 127.0.0.1 TCP，端口由系统分配（bind 0）。**
不用 AF_UNIX——Windows 版 QEMU 不保证支持；不用固定端口——多台虚拟机或
多个测试并发时会撞。

## 线程模型（接 Godot 时的坑）

`SerialChannel.DataReceived` 在**后台线程**上触发。Godot 侧订阅者必须用
`CallDeferred` 把数据转回主线程再喂给 godot-xterm，直接在后台线程碰 Node 会炸。
交换机也跑在自己的线程上，与 Godot 帧率完全解耦。
