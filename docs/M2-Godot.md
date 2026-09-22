# M2 — Godot 外壳

目标：把 M0/M1 验证过的技术脊椎接进 Godot，让概要书 MVP 的一到五条
在游戏里真正跑起来——两台真实虚拟机、各自一个可用终端、经自研交换机组网、
ping 通、隐藏控制通道发出「任务完成」。

## 结构

```
src/GameHacker.Godot/
  project.godot            Godot 4.7 + C#
  fetch-addons.sh          取 godot-xterm v4.0.3（三端预编译二进制，不入库）
  scenes/LevelSelect.tscn  选关界面（主场景）
  scenes/Level.tscn        关卡：每台机器一个 Terminal + 任务目标 + 抓包面板
  levels/*.json            关卡定义，见 docs/关卡系统.md
  scripts/
    GamePaths.cs           定位 QEMU 与镜像（它们不能进 .pck）
    TerminalBridge.cs      SerialChannel <-> godot-xterm Terminal
    VmSession.cs           一台客户机的完整会话
    GameState.cs           autoload：关卡目录、进度、当前关卡
    LevelSelect.cs         选关
    Level.cs               按关卡定义拉起机器、推进步骤（前身是 M2 的 Main.cs）
```

引用方向是**外壳引用 Core**，反过来不行：Core 里一旦出现 Godot 类型，
三端 CI 上的 `dotnet test` 就跑不起来了。

## 跑起来

```bash
bash src/GameHacker.Godot/fetch-addons.sh     # 取 godot-xterm
dotnet build GameHacker.slnx
godot-mono --path src/GameHacker.Godot        # 注意是 godot-mono，不是 godot
```

Arch 的 `godot` 包**不带 C# 支持**，会报 `No loader found for resource: *.cs`。
另外首次运行前要 `godot-mono --headless --path . --import` 建 `.godot` 缓存，
否则 GDExtension 不会被扫描到，`Terminal` 节点会变成占位符。

截图验收（CI 可用）：

```bash
GAMEHACKER_SCREENSHOT=/tmp/shot.png godot-mono --path src/GameHacker.Godot
```

## 资源不能放进 res://

导出后 `res://` 会打包成 `.pck`，里面的文件**既不能执行也不能写**。
QEMU 二进制要能 exec，磁盘镜像要能写，所以必须是可执行文件旁边的普通外部目录。
`GamePaths` 负责这件事：编辑器里按仓库布局找，导出后按可执行文件同级目录找。
Steam 按平台分 depot，每端只带自己那份 `runtime/`。

## 三个实打实踩出来的 bug

### 1. 孤儿 QEMU 进程（严重）

`_ExitTree` 写成 `async void` 再 await `DisposeAsync` 是错的 ——
**引擎不会等它**，宿主进程在杀掉子进程之前就退出了。

TCG 是纯软件模拟，每台虚拟机占满一个核。孤儿堆积会让后续启动越来越慢
直至超时，表现为「时好时坏的启动失败」，极难定位。对发行版游戏更严重：
玩家崩溃一次，机器上就永久多一个跑满一核的后台进程。

两道防线：

- `QemuLauncher.Dispose()` 同步杀进程树并 `WaitForExit`，`_ExitTree` 调它。
- `VmProcessRegistry` 用 pid 文件登记每个拉起的 QEMU，下次启动前回收孤儿。
  回收时同时核对 **pid、进程名前缀、启动时间**，三者都对上才动手 ——
  pid 会被系统复用，认错了就是杀别人的进程。
  用 pid 文件而不是扫进程表，是因为跨平台读取任意进程的命令行要各写一套
  （Linux 读 `/proc`，Windows 走 WMI，macOS 调 `ps`），而 pid 加启动时间
  完全走 `System.Diagnostics.Process` 的可移植 API。

### 2. ControlChannel 的事件分发是破坏性单消费者（Core 的设计缺陷）

原实现用一个共享 `Channel<JsonObject>` 让所有等待者去抢。
**任意两个并发的等待者会互相吞掉对方的事件。**

实际发作过程：headless 下没有布局，`Terminal` 的 `size_changed` 报出
`rows=0`，`TerminalBridge` 立刻下发一条 `resize`。那条 resize 的等待者
先于 `WaitReadyAsync` 启动，从共享队列里读到 `ready`、发现不是自己要的、
**直接丢弃**，于是 `WaitReadyAsync` 永远等不到，90 秒后超时。

M1 的集成测试没有并发等待者，所以这个缺陷一直没暴露。

改成按订阅广播：每个等待者一个独立队列。另外保留一个 64 条的历史窗口，
新订阅者先收到历史再收新的 —— 必须有，因为 **`ready` 信标只发一次**，
纯广播模型会把订阅之前到达的它直接丢掉。

### 3. rows=0 的 resize 不能下发

headless 无布局时 Terminal 报 `(cols, 0)`。把它发给客户机会让 `stty`
设出一个 0 行的终端。`TerminalBridge` 现在会过滤掉任何非正的尺寸。

## 稳定性复跑

修复后在干净环境下连跑六次，**六次全部「任务完成」，零残留 QEMU**。
只有第一次回收到 2 个孤儿（调试期间的残留），后五次一个都没有 ——
说明同步清理确实不再产生孤儿。

```bash
for i in $(seq 6); do
  GAMEHACKER_BOOT_TIMEOUT=30 timeout 80 godot-mono --headless \
    --path src/GameHacker.Godot > /tmp/try$i.log 2>&1
done
grep -l 任务完成 /tmp/try*.log | wc -l     # 期望 6
pgrep -c -x qemu-system-x86_64             # 期望 0
```

上面第 2 个缺陷已经加了回归测试（`ControlChannelTests`）。
反向验证过：把事件分发改回破坏性单消费者，六项里有两项立刻变红。

## 诊断能力（排查这些 bug 时加的，值得留着）

`QemuLauncher.DescribeFailure()` 一次性给出：进程是否还在、退出码、
两条通道的连接状态**和累计收到的字节数**、QEMU 的 stderr、以及完整命令行
（带引号，可直接复制到终端复现）。

字节数是关键判据：console 有字节说明客户机在启动，问题在 ttyS1 或判定逻辑；
console 也没字节说明客户机根本没跑起来。

`ControlChannel.SeenLines` 记录通道上出现过的每一行原始文本（含解析不了的）。
上面第 2 个 bug 就是靠它一眼看出来的 —— 日志里明明有
`{"ev":"ready",...}`，但等待者说没收到。

## 线程模型

`SerialChannel.DataReceived` 在**后台线程**触发，Godot 的 Node 只能在主线程碰。
`TerminalBridge` 的做法是后台线程只入队，真正的 `write()` 在 `_Process` 里做。
用队列而不是 `CallDeferred` 还有个好处：终端输出是突发的，
一帧内攒到的若干段可以合并成一次 `write()`。

反向的 `data_sent` 信号不只是玩家键盘输入 —— libtsm 对 DSR（`ESC[6n`）、
设备属性查询的应答也从这里出来，客户机的 shell 提示符依赖这些应答，
不转发回去 shell 会错乱。
