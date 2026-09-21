"""M0 编排原型。

这里的每个类都对应将来 GameHacker.Core 里的一个 C# 类型，
先用 Python 把协议和时序跑通，再照着翻译成 C#：

    Listener  -> SerialChannel / ControlChannel 的 TcpListener 部分
    Vm        -> QemuLauncher
    ctl_*     -> ControlChannel 的请求/应答

核心约定（三端可移植的前提）：
  * 一律 127.0.0.1 TCP，不用 AF_UNIX —— Windows 版 QEMU 不保证支持
  * 宿主先监听，QEMU 当客户端连进来（server=off + reconnect-ms）
    这样宿主永远先就位，开机第一个字节都不会丢，VM 重启也能自动接回
"""
import json, os, socket, subprocess, threading, time

QEMU = os.environ.get("QEMU", "qemu-system-x86_64")
HOST = "127.0.0.1"


class Listener:
    """一条串口通道的宿主端：先 listen，等 QEMU 连进来。"""

    def __init__(self, port, name=""):
        self.port, self.name = port, name
        self.buf = b""
        self.sock = None
        self._lock = threading.Lock()
        self._stop = False
        self._srv = socket.socket()
        self._srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._srv.bind((HOST, port))
        self._srv.listen(1)
        threading.Thread(target=self._serve, daemon=True).start()

    def _serve(self):
        while not self._stop:
            try:
                conn, _ = self._srv.accept()
            except OSError:
                return
            conn.settimeout(0.3)
            self.sock = conn
            while not self._stop:
                try:
                    data = conn.recv(8192)
                except socket.timeout:
                    continue
                except OSError:
                    break
                if not data:
                    break
                with self._lock:
                    self.buf += data
            self.sock = None          # QEMU 会靠 reconnect-ms 自己连回来

    def send(self, data: bytes):
        s = self.sock
        if s:
            s.sendall(data)

    def text(self):
        with self._lock:
            return self.buf.decode("utf-8", "replace")

    def close(self):
        self._stop = True
        try:
            self._srv.close()
        except OSError:
            pass


class Vm:
    """一台 M0 客户机。"""

    IMAGES = os.path.join(os.path.dirname(__file__), "..", "images")

    def __init__(self, idx, name, ip, netdev, runlog, disk=None, mem=256,
                 extra_args=(), extra_append="", nic="e1000e", bus="ahci"):
        # extra_args / extra_append 给伪装（-smbios / -cpu / m0.uts_*）留的口子。
        # 这份是探针用的平行实现，真正发给玩家的命令行在
        # GameHacker.Core 的 QemuLauncher 里，两边要一起改。
        self.idx, self.name, self.ip = idx, name, ip
        base = 17000 + idx * 100
        self.con = Listener(base + 1, f"{name}:con")   # ttyS0 玩家终端
        self.ctl = Listener(base + 2, f"{name}:ctl")   # ttyS1 隐藏控制通道
        self.qmp_port = base + 3

        img = lambda f: os.path.abspath(os.path.join(self.IMAGES, f))
        chardev = lambda i, p: f"socket,id={i},host={HOST},port={p},server=off,reconnect-ms=1000"

        # 带 disk 时 initramfs 会 switch_root 进去（主角机 = 完整 Alpine），
        # 不带则就地当纯内存的极小目标机跑。
        # mke2fs 造的是整盘文件系统、没有分区表，所以根设备是 /dev/vda 而不是 vda1。
        root_arg = (" m0.root=/dev/vda" if bus == "virtio" else " m0.root=/dev/sda") if disk else ""

        self.args = [
            QEMU,
            "-machine", "q35,accel=tcg", "-m", str(mem), "-smp", "1",
            "-display", "none", "-vga", "none", "-monitor", "none",
            "-kernel", img("vmlinuz-virt"), "-initrd", img("m0-guest.cpio.gz"),
            # quiet/loglevel 让玩家看到的是干净终端，不是内核刷屏
            "-append", f"console=ttyS0 quiet loglevel=3 tsc=unstable "
                       f"m0.host={name} m0.ip={ip}{root_arg}{extra_append}",
            "-chardev", chardev("con", base + 1), "-serial", "chardev:con",
            "-chardev", chardev("ctl", base + 2), "-serial", "chardev:ctl",
            "-netdev", f"stream,id=n0,{netdev}",
            # romfile= 关掉 PXE 引导 ROM：我们永远不网络引导，
            # 留着就得多发一个 efi-virtio.rom，还白占客户机内存
            "-device", f"{nic},netdev=n0,romfile=,mac=52:54:00:00:00:{idx:02x}",
            "-qmp", f"tcp:{HOST}:{self.qmp_port},server=on,wait=off",
        ]
        self.args += list(extra_args)
        if disk:
            if bus == "virtio":
                self.args += ["-drive", f"file={img(disk)},if=virtio,format=qcow2,snapshot=on"]
            else:
                # q35 的 if=ide 不会接到 ich9-ahci 上，必须显式挂 ide-hd 到 ide.0
                self.args += ["-drive", f"file={img(disk)},if=none,id=d0,format=qcow2,snapshot=on",
                              "-device", "ide-hd,drive=d0,bus=ide.0"]
        self._log = open(runlog, "wb")
        self.proc = subprocess.Popen(self.args, stdout=self._log, stderr=self._log)

    def _scan(self, seen):
        """从 ctl 缓冲区的 seen 偏移起，解析出完整的 JSON 事件行。"""
        with self.ctl._lock:
            chunk, seen = self.ctl.buf[seen:], len(self.ctl.buf)
        evs = []
        for raw in chunk.split(b"\n"):
            raw = raw.strip()
            if not raw:
                continue
            try:
                evs.append(json.loads(raw))
            except ValueError:
                pass                  # 串口上的开机噪声，忽略
        return evs, seen

    def wait_ready(self, timeout=60.0):
        """等客户机 init 发出 ready 信标。

        必须等：QEMU 一启动就把 chardev 连上了（~0.1s），但客户机要到 ~1.5s
        才起 ttyS1 的读取循环。在那之前发的命令会被直接丢掉。
        """
        seen, deadline = 0, time.time() + timeout
        while time.time() < deadline:
            evs, seen = self._scan(seen)
            for ev in evs:
                if ev.get("ev") == "ready":
                    return ev
            time.sleep(0.05)
        return None

    def ctl_request(self, line, want, timeout=25.0, retry=2.0):
        """往 ttyS1 发一行命令，等回一条 ev==want 的 JSON。

        命令按 retry 间隔重发：串口是不可靠的裸字节流，没有应答层，
        重发是这里最省事也最可靠的做法（命令都设计成幂等的）。
        """
        seen = len(self.ctl.buf)
        deadline, next_send = time.time() + timeout, 0.0
        while time.time() < deadline:
            if time.time() >= next_send:
                self.ctl.send((line + "\n").encode())
                next_send = time.time() + retry
            evs, seen = self._scan(seen)
            for ev in evs:
                if ev.get("ev") == want:
                    return ev
            time.sleep(0.05)
        return None

    def kill(self):
        for ch in (self.con, self.ctl):
            ch.close()
        self.proc.kill()
        self.proc.wait(timeout=5)
        self._log.close()
