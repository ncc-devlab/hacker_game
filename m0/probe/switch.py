#!/usr/bin/env python3
"""用户态二层学习交换机 —— GameHacker.Core 里 VirtualSwitch.cs 的 Python 原型。

线上格式（已对 qemu-11.1.1 源码核实）：
    net/stream_data.c:34   uint32_t len = htonl(size)
    net/net.c:2100         net_fill_rstate() 状态机: 4 字节大端长度 + 裸以太网帧
不开 vnet_hdr 就没有第二个长度字段。

这就是全部的「交换机状态」：一张 MAC -> 端口 的表。
不需要 STP（拓扑是我们自己造的星型，物理上不可能成环）、
不需要 VLAN（等某个关卡真要用再加）、不需要 ARP/IP 逻辑（二层不关心）、
不需要分片重组（QEMU 给的就是完整帧）、不需要校验和（客户机内核的事）。
"""
import argparse, socket, struct, sys, threading, time

BROADCAST = b"\xff" * 6
AGE_SECONDS = 300


def mac_str(m): return ":".join(f"{b:02x}" for b in m)


class Switch:
    def __init__(self, port, host="127.0.0.1", pcap=None, verbose=False):
        self.ports = {}                 # port_id -> socket
        self.mac_table = {}             # mac bytes -> (port_id, last_seen)
        self.lock = threading.Lock()
        self.verbose = verbose
        self.frames = 0
        self._next_id = 0
        self._pcap = self._open_pcap(pcap) if pcap else None
        self._srv = socket.socket()
        self._srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._srv.bind((host, port))
        self._srv.listen(8)

    # --- 抓包：交换机是所有流量的唯一入口，写 pcap 几乎零成本 ---
    def _open_pcap(self, path):
        f = open(path, "wb")
        f.write(struct.pack("<IHHiIII", 0xA1B2C3D4, 2, 4, 0, 0, 65535, 1))  # LINKTYPE_ETHERNET
        return f

    def _dump(self, frame):
        if not self._pcap:
            return
        t = time.time()
        self._pcap.write(struct.pack("<IIII", int(t), int(t % 1 * 1e6), len(frame), len(frame)))
        self._pcap.write(frame)
        self._pcap.flush()

    def serve_forever(self):
        while True:
            conn, _ = self._srv.accept()
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            with self.lock:
                pid = self._next_id
                self._next_id += 1
                self.ports[pid] = conn
            threading.Thread(target=self._port_loop, args=(pid, conn), daemon=True).start()

    def _port_loop(self, pid, conn):
        buf = b""
        try:
            while True:
                data = conn.recv(65536)
                if not data:
                    break
                buf += data
                # 拆帧：4 字节大端长度 + 载荷，可能粘包/半包
                while len(buf) >= 4:
                    n = struct.unpack(">I", buf[:4])[0]
                    if n > 65535:
                        raise ValueError(f"帧长异常 {n}，成帧已失步")
                    if len(buf) < 4 + n:
                        break
                    frame, buf = buf[4:4 + n], buf[4 + n:]
                    self._forward(pid, frame)
        except (OSError, ValueError) as e:
            if self.verbose:
                print(f"[switch] 端口 {pid} 断开: {e}", file=sys.stderr)
        finally:
            with self.lock:
                self.ports.pop(pid, None)
                for mac, (p, _) in list(self.mac_table.items()):
                    if p == pid:
                        del self.mac_table[mac]
            conn.close()

    def _forward(self, src_port, frame):
        if len(frame) < 14:
            return
        dst, src = frame[0:6], frame[6:12]
        self.frames += 1
        self._dump(frame)

        with self.lock:
            now = time.time()
            self.mac_table[src] = (src_port, now)                       # 源 MAC 学习
            for mac, (_, seen) in list(self.mac_table.items()):         # 老化
                if now - seen > AGE_SECONDS:
                    del self.mac_table[mac]
            entry = self.mac_table.get(dst)
            # 目的已知 -> 单播；未知 / 广播 / 组播（首字节最低位为 1）-> 泛洪
            if entry and not (dst[0] & 1):
                targets = [entry[0]] if entry[0] != src_port else []
            else:
                targets = [p for p in self.ports if p != src_port]
            socks = [(p, self.ports[p]) for p in targets if p in self.ports]

        if self.verbose:
            print(f"[switch] {mac_str(src)} -> {mac_str(dst)} "
                  f"{len(frame):>4}B  入口{src_port} 出口{[p for p, _ in socks]}")

        hdr = struct.pack(">I", len(frame))
        for p, s in socks:
            try:
                s.sendall(hdr + frame)
            except OSError:
                pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=17900)
    ap.add_argument("--pcap", help="把所有帧写成 pcap，可直接用 Wireshark 打开")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    sw = Switch(a.port, pcap=a.pcap, verbose=a.verbose)
    print(f"[switch] 监听 127.0.0.1:{a.port}" + (f"  抓包 -> {a.pcap}" if a.pcap else ""))
    sw.serve_forever()


if __name__ == "__main__":
    main()
