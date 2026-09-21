#!/usr/bin/env python3
"""录一份真实的 ttyS0 字节流，供离线喂给不同的终端模拟器做对照。

这是「要不要魔改 godot-xterm」这个问题的证据来源：与其读源码猜，
不如把同一段真实流量分别喂给 pyte 和 libtsm（godot-xterm 的引擎），
看谁渲染成什么样。
"""
import os, sys, time
sys.path.insert(0, os.path.dirname(__file__))
from m0lib import Vm

RUN = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "run"))
OUT = os.path.join(RUN, "session.raw")
COLS, ROWS = 100, 30


def main():
    vm = Vm(1, "main", "10.0.0.1/24",
            "addr.type=inet,addr.host=127.0.0.1,addr.port=17900,server=off,reconnect-ms=1000",
            os.path.join(RUN, "vm1.log"), disk="alpine-main.qcow2", mem=512)
    try:
        if not vm.wait_ready(timeout=90):
            print("FAIL: 客户机没起来", file=sys.stderr)
            return 2
        vm.ctl_request(f"resize {ROWS} {COLS}", "resize", timeout=15)
        time.sleep(1)

        # 客户机会发 DSR (CSI 6n) 问光标位置。这里刻意*不*应答，
        # 录到的就是纯粹的客户机输出，不掺宿主的回复。
        def send(data, wait=1.2):
            vm.con.send(data)
            time.sleep(wait)

        send(b"printf '\\033[31mRED\\033[0m \\033[38;5;208m256COLOR\\033[0m "
             b"\\033[38;2;0;200;100mTRUECOLOR\\033[0m \\033[4mUNDERLINE\\033[0m\\n'\n")
        send(b"vim /tmp/cap.txt\n", 3)
        send(b"iVIM-LINE-ONE\rVIM-LINE-TWO\x1b", 2)
        send(b":set number\r", 1.5)
        send(b":wq\r", 2)
        send(b"tmux new-session -s cap\n", 4)
        send(b'\x02"', 2)                      # C-b " 上下分屏
        send(b"echo PANE-BOTTOM\n", 1.5)
        send(b"\x02o", 1.5)                    # C-b o 切 pane
        send(b"echo PANE-TOP\n", 2)
        send(b"\x02&y", 2)                     # C-b & 杀会话

        os.makedirs(RUN, exist_ok=True)
        with open(OUT, "wb") as f:
            f.write(vm.con.buf)
        print(f"录到 {len(vm.con.buf)} 字节 -> {OUT}")
        return 0
    finally:
        vm.kill()


if __name__ == "__main__":
    sys.exit(main())
