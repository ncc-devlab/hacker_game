#!/usr/bin/env python3
"""隐藏控制通道（ttyS1）客户端。

M0 用它验证宿主 <-> 客户机的带外链路：发一行命令，收 JSON 事件。
将来 Godot 侧的 ControlChannel.cs 就是这个东西的 C# 版本。
"""
import argparse, json, socket, sys, time


def connect(port, host="127.0.0.1", timeout=30.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            s = socket.create_connection((host, port), timeout=2.0)
            s.settimeout(1.0)
            return s
        except OSError:
            time.sleep(0.2)
    raise SystemExit(f"连不上控制通道 {host}:{port}")


def request(sock, line, want, timeout=20.0):
    """发一行命令，等到 ev == want 的那条 JSON 事件。"""
    sock.sendall((line + "\n").encode())
    buf, deadline = b"", time.time() + timeout
    while time.time() < deadline:
        try:
            chunk = sock.recv(4096)
        except socket.timeout:
            continue
        if not chunk:
            break
        buf += chunk
        while b"\n" in buf:
            raw, buf = buf.split(b"\n", 1)
            raw = raw.strip()
            if not raw:
                continue
            try:
                ev = json.loads(raw)
            except ValueError:
                continue              # 串口上的开机噪声，忽略
            if ev.get("ev") == want:
                return ev
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True, help="ttyS1 的 TCP 端口")
    ap.add_argument("--ping", metavar="IP", help="让客户机 ping 这个地址")
    ap.add_argument("--wait-boot", type=float, default=30.0)
    args = ap.parse_args()

    sock = connect(args.port, timeout=args.wait_boot)

    st = request(sock, "status", "status", timeout=args.wait_boot)
    if not st:
        print("FAIL: 控制通道无响应（客户机可能还没起来）", file=sys.stderr)
        return 2
    print(f"  控制通道就绪: {st}")

    if args.ping:
        ev = request(sock, f"ping {args.ping}", "ping", timeout=25.0)
        if not ev:
            print("FAIL: ping 命令超时", file=sys.stderr)
            return 3
        print(f"  ping 结果: {ev}")
        return 0 if ev.get("result") == "ok" else 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
