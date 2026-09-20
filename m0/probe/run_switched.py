#!/usr/bin/env python3
"""M0-b：两台 VM 经自研交换机互通，并把全部流量抓成 pcap。

跟 M0-a 的唯一差别是中间多了 switch.py。所以这一级一旦挂，
故障一定在交换机，不可能在镜像或 QEMU 参数 —— 这就是阶梯验证的价值。
"""
import os, sys, threading, time
sys.path.insert(0, os.path.dirname(__file__))
from m0lib import Vm
from switch import Switch

RUN = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "run"))
NET_PORT = 17900


def main():
    os.makedirs(RUN, exist_ok=True)
    pcap = os.path.join(RUN, "m0.pcap")
    sw = Switch(NET_PORT, pcap=pcap, verbose="-v" in sys.argv)
    threading.Thread(target=sw.serve_forever, daemon=True).start()
    print(f"  交换机监听 127.0.0.1:{NET_PORT}，抓包 -> {pcap}")

    # 两台 VM 都当客户端接入交换机，断线自动重连
    cli = f"addr.type=inet,addr.host=127.0.0.1,addr.port={NET_PORT},server=off,reconnect-ms=1000"
    vms = [Vm(1, "alpha", "10.0.0.1/24", cli, os.path.join(RUN, "vm1.log")),
           Vm(2, "beta",  "10.0.0.2/24", cli, os.path.join(RUN, "vm2.log"))]
    try:
        t0 = time.time()
        for vm in vms:
            ev = vm.wait_ready(timeout=60)
            if not ev:
                print(f"FAIL: {vm.name} 没发出 ready 信标", file=sys.stderr)
                return 2
            print(f"  {vm.name:>5} 就绪 ({time.time()-t0:.1f}s)  {ev}")

        ok = True
        for src, dst_ip in ((vms[1], "10.0.0.1"), (vms[0], "10.0.0.2")):
            ev = src.ctl_request(f"ping {dst_ip}", "ping", timeout=30)
            print(f"  {src.name:>5} -> {dst_ip} : {ev}")
            ok = ok and bool(ev) and ev.get("result") == "ok"

        print()
        print(f"  交换机转发 {sw.frames} 帧，MAC 表:")
        for mac, (p, _) in sw.mac_table.items():
            print(f"    {':'.join(f'{b:02x}' for b in mac)}  -> 端口 {p}")
        print()
        print("\033[32mPASS\033[0m  M0-b 经自研交换机打通" if ok else "\033[31mFAIL\033[0m")
        return 0 if ok else 1
    finally:
        for vm in vms:
            vm.kill()


if __name__ == "__main__":
    sys.exit(main())
