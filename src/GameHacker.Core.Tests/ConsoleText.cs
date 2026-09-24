using System.Text;

namespace GameHacker.Core.Tests;

/// <summary>
/// 把客户机控制台的字节流攒成文本。
/// </summary>
/// <remarks>
/// <para><b>必须用有状态的解码器。</b> 串口是字节流，一个 UTF-8 字符完全可能被切在
/// 两次读取之间。每块各自 <c>Encoding.UTF8.GetString</c> 的话，跨块的那个字就碎成
/// 替换字符 —— 中文断言明明该命中却找不到，而失败信息里那段文本看上去只是「有点乱码」。
/// 实测踩到过。</para>
/// <para>线程安全：串口的接收回调在后台线程上跑，断言在测试线程上读。</para>
/// </remarks>
public sealed class ConsoleText
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();

    public void Append(ReadOnlySpan<byte> bytes)
    {
        // GetCharCount(flush: false) 会把半个字符留在解码器里，等下一块补齐
        Span<char> chars = stackalloc char[_decoder.GetCharCount(bytes, flush: false)];
        int written = _decoder.GetChars(bytes, chars, flush: false);
        lock (_text) _text.Append(chars[..written]);
    }

    public override string ToString()
    {
        lock (_text) return _text.ToString();
    }

    public bool Contains(string what) => ToString().Contains(what);

    /// <summary>末尾几行，够看清最后几条命令说了什么。</summary>
    public string Tail(int lines = 12) =>
        string.Join('\n', ToString().Replace("\r", "").Split('\n').TakeLast(lines));
}
