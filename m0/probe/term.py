"""宿主侧终端模拟器 —— 用来验证客户机送出来的 ANSI 字节流。

为什么需要它：M0-c 要验证的是「vim/tmux 渲染正确」，但"正确"没法靠肉眼
在自动化测试里断言。做法是在宿主上跑一个真正的 xterm 兼容模拟器（pyte），
把 ttyS0 的原始字节喂进去，然后对渲染出来的屏幕做文本断言。

这么做还把两个风险解耦了：
  * 字节流对不对          -> 这里验证，是我们自己的责任
  * godot-xterm 渲染对不对 -> 那是它的责任
本层绿了之后，godot-xterm 里的任何不一致都能直接定位到它身上。
"""
import re, time

import pyte

ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[a-zA-Z]")

# pyte 0.8.2 会把带私有前缀的 CSI（`CSI ? ... x` / `CSI > ... x`）也派发给
# 普通处理函数，但那些函数不接受 private 关键字，于是直接抛 TypeError。
# 真实 xterm 对这些私有变体要么忽略、要么另作处理，这里逐个兜住。
#
# ⚠ 这两条序列都值得盯着 godot-xterm：
#   select_graphic_rendition <- vim 发的 XTMODKEYS (`CSI > 4 ; 2 m`)
#   report_device_status     <- shell/tmux 发的 DSR (`CSI ? 6 n`)
_PRIVATE_TOLERANT = ("select_graphic_rendition",)


def _tolerate_private(name):
    base = getattr(pyte.Screen, name)

    def wrapper(self, *args, private=False, **kw):
        if private:
            return                      # 真实 xterm 不对这些私有变体做渲染处理
        return base(self, *args, **kw)

    wrapper.__name__ = name
    return wrapper


class _Screen(pyte.Screen):
    """pyte.Screen 的补丁版：容忍私有 CSI，并像真终端一样应答 DSR。"""

    def __init__(self, cols, rows, respond=None):
        super().__init__(cols, rows)
        self._respond = respond

    def report_device_status(self, mode, private=False):
        """DSR。真实终端必须应答，否则 shell 和全屏程序会错乱。

        客户机的 busybox ash 提示符就会发 `CSI 6 n` 问光标位置。
        godot-xterm 也必须实现这条 —— 这里顺便验证了这条反向链路。
        """
        if not self._respond:
            return
        if mode == 6:                   # CPR：回报光标位置
            self._respond(f"\x1b[{self.cursor.y + 1};{self.cursor.x + 1}R".encode())
        elif mode == 5:                 # 设备状态：一切正常
            self._respond(b"\x1b[0n")


for _n in _PRIVATE_TOLERANT:
    setattr(_Screen, _n, _tolerate_private(_n))


class TerminalView:
    """把一路串口字节流渲染成屏幕，并提供等待/断言用的查询。"""

    def __init__(self, cols=80, rows=24, respond=None):
        self.cols, self.rows = cols, rows
        self.screen = _Screen(cols, rows, respond=respond)
        self.stream = pyte.ByteStream(self.screen)
        self._fed = 0
        self._raw = []          # 全量流水，见 raw_text()

    def feed_from(self, listener):
        """把 listener 缓冲区里还没喂过的字节喂进模拟器。"""
        with listener._lock:
            buf = listener.buf
        if len(buf) > self._fed:
            chunk = buf[self._fed:]
            self.stream.feed(chunk)
            self._raw.append(chunk)
            self._fed = len(buf)

    def resize(self, cols, rows):
        self.cols, self.rows = cols, rows
        self.screen.resize(rows, cols)

    def display(self):
        return [line.rstrip() for line in self.screen.display]

    def text(self):
        """当前屏幕的可见内容。用于断言 TUI 布局（vim 的 ~ 列、tmux 的分屏）。"""
        return "\n".join(self.display())

    def raw_text(self):
        """全量流水（剥掉 ANSI）。用于断言命令输出。

        为什么要两种数据源：pyte 默认没有回滚缓冲，命令输出一旦滚出可视区
        就从 text() 里消失了。断言「某条命令打印了什么」必须查流水，
        断言「屏幕现在长什么样」才查 text()。两者混用会得到随屏幕高度
        漂移的假阴性。
        """
        joined = b"".join(self._raw).decode("utf-8", "replace")
        return ANSI_RE.sub("", joined).replace("\r", "")

    def wait_for(self, needle, listener, timeout=20.0, poll=0.1, source="screen"):
        """等某段文本出现。needle 可以是 str 或已编译的正则。

        source="screen" 查当前屏幕，source="raw" 查全量流水（见 raw_text()）。
        """
        deadline = time.time() + timeout
        while time.time() < deadline:
            self.feed_from(listener)
            haystack = self.text() if source == "screen" else self.raw_text()
            hit = (needle.search(haystack) if hasattr(needle, "search")
                   else needle in haystack)
            if hit:
                return True
            time.sleep(poll)
        self.feed_from(listener)
        return False

    def cell_fg(self, row, col):
        return self.screen.buffer[row][col].fg

    def dump(self, title=""):
        bar = "-" * (self.cols + 2)
        out = [f"{title} ({self.cols}x{self.rows})", bar]
        out += [f"|{line:<{self.cols}}|" for line in self.screen.display]
        out.append(bar)
        return "\n".join(out)
