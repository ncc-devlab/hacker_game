#!/usr/bin/env python3
"""M0-c：终端渲染验收。

对应概要书 MVP 第 2 条和第 6 条：串口裸字节终端、vim/tmux（含分屏）、
ANSI 渲染、TERM 设置、resize 转发。

验证方式不是肉眼看，而是在宿主上跑真正的 xterm 兼容模拟器（pyte）
渲染 ttyS0 的原始字节流，再对渲染结果做断言。详见 term.py 的说明。

两类断言用两种数据源，不能混：
  * 命令输出（stty size / cat 文件）-> raw_text()，全量流水
  * TUI 布局（vim 的 ~ 列 / tmux 分屏）-> text()，当前屏幕
pyte 默认没有回滚缓冲，命令输出滚出可视区就从 text() 里消失了。
"""
import os, re, sys, time
sys.path.insert(0, os.path.dirname(__file__))
from m0lib import Vm
from term import TerminalView

RUN = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "run"))
COLS, ROWS = 100, 30

# 成对哨兵界定一条命令的输出。
#
# 写成拼接字符串（"M0""_BEG"），这样字面量 M0_BEG 只出现在命令的*输出*里，
# 不会出现在终端对输入的回显里。
#
# 为什么要成对而不是按下标切：raw_text() 每次都对全量缓冲重新剥 ANSI，
# 而转义序列被块边界切成两半时剥不干净，同一段前缀在不同时刻长度不同。
# 按下标切会随机漏内容 —— 这正是之前 TERM 和 cat 两项假阴性的原因。
# 哨兵必须<b>每条命令唯一</b>。用固定字样会出一个隐蔽的竞态：
# M0_DONE 一旦出现就永久留在流水里，下一次 wait_for 立刻命中、
# 根本没等新输出，rfind 找回的还是上一条命令的区间。
# 加序号之后每次等的都是本次这一条。
_seq = 0


def _sentinels():
    global _seq
    _seq += 1
    # 写成拼接字符串，这样完整字面量只出现在命令的*输出*里，
    # 不会出现在终端对输入的回显里。
    return (f"M0_BEG_{_seq}", f"M0_DONE_{_seq}",
            f'echo "M0""_BEG_{_seq}"', f'echo "M0""_DONE_{_seq}"')

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    mark = "\033[32m ok \033[0m" if ok else "\033[31mFAIL\033[0m"
    print(f"  [{mark}] {name}" + (f"  {detail}" if detail else ""), flush=True)
    return ok


def sh(vm, view, cmd, timeout=20):
    """在客户机 shell 里跑一条命令，返回夹在本次哨兵之间的那段输出。"""
    beg, done, beg_cmd, done_cmd = _sentinels()
    vm.con.send(f"{beg_cmd}; {cmd}; {done_cmd}\n".encode())
    if not view.wait_for(done, vm.con, timeout=timeout, source="raw"):
        return ""
    raw = view.raw_text()
    end = raw.rfind(done)
    start = raw.rfind(beg, 0, end)
    return raw[start + len(beg):end] if start >= 0 else ""


def main():
    os.makedirs(RUN, exist_ok=True)
    vm = Vm(1, "main", "10.0.0.1/24",
            "addr.type=inet,addr.host=127.0.0.1,addr.port=17900,server=off,reconnect-ms=1000",
            os.path.join(RUN, "vm1.log"), disk="alpine-main.qcow2", mem=512)
    # respond 让模拟器像真终端一样应答 DSR —— 客户机 shell 的提示符依赖这条
    view = TerminalView(80, 24, respond=vm.con.send)
    try:
        t0 = time.time()
        if not vm.wait_ready(timeout=90):
            print("FAIL: 客户机没起来", file=sys.stderr)
            return 2
        print(f"  Alpine 主角机就绪 ({time.time() - t0:.1f}s)\n", flush=True)
        sh(vm, view, "true", timeout=30)

        # --- 1. TERM 与 resize 转发 ------------------------------------
        # 裸串口不是 PTY，没有 TIOCSWINSZ，尺寸只能靠 ttyS1 控制通道传
        ev = vm.ctl_request(f"resize {ROWS} {COLS}", "resize", timeout=15)
        check("resize 经 ttyS1 下发", ev, str(ev))
        view.resize(COLS, ROWS)

        out = sh(vm, view, "stty size")
        check("客户机 stty size 已生效", f"{ROWS} {COLS}" in out,
              f"实测 {out.strip().splitlines()[:2]}")
        out = sh(vm, view, "echo TERM=$TERM")
        check("TERM=xterm-256color", "TERM=xterm-256color" in out)

        # --- 2. SGR 颜色（查屏幕，要看单元格属性）-----------------------
        vm.con.send(b"printf '\\033[31mRED\\033[0m\\033[32mGREEN\\033[0m\\n'\n")
        view.wait_for("REDGREEN", vm.con, timeout=15)
        row = next((r for r, line in enumerate(view.display())
                    if line.strip().endswith("REDGREEN")), None)
        if row is None:
            check("SGR 前景色解析正确", False, "屏幕上没找到输出行")
        else:
            col = view.display()[row].index("REDGREEN")
            fg_r, fg_g = view.cell_fg(row, col), view.cell_fg(row, col + 3)
            check("SGR 前景色解析正确", (fg_r, fg_g) == ("red", "green"),
                  f"实测 {fg_r}/{fg_g}")

        # --- 3. vim：全屏 TUI -------------------------------------------
        vm.con.send(b"vim /tmp/m0.txt\n")
        # vim 进备用屏后用 ~ 填充空行，这是它已接管全屏的标志
        check("vim 进入备用屏（~ 填充列）", view.wait_for("~", vm.con, timeout=25))
        vm.con.send(b"iHELLO-FROM-VIM\x1b")            # i 进插入模式, Esc 退出
        check("vim 缓冲区内容渲染正确",
              view.wait_for("HELLO-FROM-VIM", vm.con, timeout=15))
        vm.con.send(b":set number\r")
        check("vim 行号渲染",
              view.wait_for(re.compile(r"^\s+1\s+HELLO-FROM-VIM", re.M),
                            vm.con, timeout=15))
        vm.con.send(b":wq\r")
        time.sleep(2)
        out = sh(vm, view, "cat /tmp/m0.txt")
        check("vim 真的写了文件", "HELLO-FROM-VIM" in out)

        # --- 4. tmux：分屏 ----------------------------------------------
        vm.con.send(b"tmux new-session -s m0\n")
        ok_status = view.wait_for("[m0]", vm.con, timeout=25)
        if not check("tmux 状态栏出现", ok_status):
            # 最常见的原因是客户机没有 pty（/dev/pts 没挂上），
            # 这时 tmux 会报 "create window failed: fork failed"
            tail = view.raw_text()[-400:]
            print(f"       tmux 输出尾部: {tail!r}", flush=True)
        else:
            vm.con.send(b'\x02"')                      # C-b " 上下分屏
            time.sleep(2)
            vm.con.send(b"echo PANE-BOTTOM\n")
            time.sleep(1.5)
            vm.con.send(b"\x02o")                      # C-b o 切到另一个 pane
            time.sleep(1.5)
            vm.con.send(b"echo PANE-TOP\n")
            view.wait_for("PANE-TOP", vm.con, timeout=20)
            screen = view.text()
            check("tmux 分屏两个 pane 同屏渲染",
                  "PANE-TOP" in screen and "PANE-BOTTOM" in screen)

            rows = [r for r, l in enumerate(view.display())
                    if "PANE-TOP" in l or "PANE-BOTTOM" in l]
            check("两个 pane 在不同行区（真的分了屏）",
                  len(rows) >= 2 and max(rows) - min(rows) > 2, f"行号 {rows}")
            vm.con.send(b"\x02&y")                     # C-b & 杀会话
            time.sleep(1)

        print()
        print(view.dump("最终屏幕"), flush=True)

    finally:
        vm.kill()

    print()
    failed = [n for n, ok, _ in results if not ok]
    if failed:
        print(f"\033[31mFAIL\033[0m  {len(failed)}/{len(results)} 项未通过: {failed}")
        return 1
    print(f"\033[32mPASS\033[0m  M0-c 终端渲染全部 {len(results)} 项通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
