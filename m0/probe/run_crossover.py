#!/usr/bin/env python3
"""M0-a：交叉网线。两台 VM 用 -netdev stream 直连，中间零自研代码。

故障域隔离：这一级绿了，说明镜像 / virtio 网卡 / QEMU 参数 / 串口通道全对。
之后把自研交换机插进中间（M0-b），任何回归都只可能是交换机的问题。
"""
import os, sys, time
sys.path.insert(0, os.path.dirname(__file__))
from m0lib import Vm

RUN = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "run"))
NET_PORT = 17900


def main():
    os.makedirs(RUN, exist_ok=True)
    # alpha 监听，beta 连接并自动重连 —— 启动顺序无关
    a = Vm(1, "alpha", "10.0.0.1/24",
           f"addr.type=inet,addr.host=127.0.0.1,addr.port={NET_PORT},server=on",
           os.path.join(RUN, "vm1.log"))
    b = Vm(2, "beta", "10.0.0.2/24",
           f"addr.type=inet,addr.host=127.0.0.1,addr.port={NET_PORT},server=off,reconnect-ms=1000",
           os.path.join(RUN, "vm2.log"))
    try:
        t0 = time.time()
        for vm in (a, b):
            ev = vm.wait_ready(timeout=60)
            if not ev:
                print(f"FAIL: {vm.name} 没发出 ready 信标", file=sys.stderr)
                return 2
            print(f"  {vm.name:>5} 就绪 ({time.time()-t0:.1f}s)  {ev}")

        ev = b.ctl_request("ping 10.0.0.1", "ping", timeout=30)
        print(f"  beta -> alpha : {ev}")
        ok = bool(ev) and ev.get("result") == "ok"

        ev2 = a.ctl_request("ping 10.0.0.2", "ping", timeout=30)
        print(f"  alpha -> beta : {ev2}")
        ok = ok and bool(ev2) and ev2.get("result") == "ok"

        print()
        print("\033[32mPASS\033[0m  M0-a 直连打通：真实以太网帧在两台 QEMU 之间双向往返"
              if ok else "\033[31mFAIL\033[0m  ping 不通")
        return 0 if ok else 1
    finally:
        a.kill(); b.kill()


if __name__ == "__main__":
    sys.exit(main())
