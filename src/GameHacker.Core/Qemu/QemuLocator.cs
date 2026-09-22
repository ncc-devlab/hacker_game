using System.Runtime.InteropServices;

namespace GameHacker.Core.Qemu;

/// <summary>
/// 找本平台的 <c>qemu-system-x86_64</c>。游戏和测试共用这一份逻辑。
/// </summary>
/// <remarks>
/// <para>顺序：<c>GAMEHACKER_QEMU</c> → 随游戏发布的 <c>runtime/&lt;平台&gt;/bin</c>
/// → 各平台的常见安装位置 → PATH。</para>
/// <para><b>为什么不能只靠 PATH。</b> QEMU 官方 Windows 安装包装到
/// <c>C:\Program Files\qemu</c> 但不改 PATH；macOS 上从 Finder / Dock 启动的程序
/// 拿到的 PATH 只有 <c>/usr/bin:/bin:/usr/sbin:/sbin</c>，看不到 Homebrew。
/// 两边在实测里都得手动设 <c>GAMEHACKER_QEMU</c> 才能跑。</para>
/// </remarks>
public static class QemuLocator
{
    public const string EnvVar = "GAMEHACKER_QEMU";

    /// <summary>当前平台在 <c>runtime/</c> 下的目录名，如 <c>windows-x86_64</c>。</summary>
    public static string PlatformDir { get; } = DescribePlatform(
        CurrentOs(), RuntimeInformation.OSArchitecture == Architecture.Arm64);

    /// <summary>
    /// 返回找到的完整路径；哪里都没有时返回 <c>null</c>。
    /// </summary>
    /// <param name="root">游戏外部资源根目录（编辑器下是仓库根）。</param>
    public static string? Find(string root) => Find(
        root, CurrentOs(), PlatformDir,
        Environment.GetEnvironmentVariable(EnvVar),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        File.Exists);

    /// <summary>可注入的版本，供单元测试模拟各平台。</summary>
    public static string? Find(string root, OSPlatform os, string platformDir,
                               string? fromEnv, string? pathVar, string? programFiles,
                               Func<string, bool> exists)
    {
        // 环境变量是显式指定，给了就用，哪怕文件不存在 —— 让后面的启动报错说清楚是哪个路径
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        bool windows = os == OSPlatform.Windows;
        string exe = windows ? "qemu-system-x86_64.exe" : "qemu-system-x86_64";
        char sep = windows ? ';' : ':';
        // 按目标平台的分隔符拼，不用 Path.Combine —— 后者跟宿主走，
        // 单元测试在 Windows 上模拟 Linux 时会拼出 /snap/bin\qemu（第三轮 Windows 验证撞上的）
        string Join(params string[] parts) => string.Join(windows ? "\\" : "/",
            parts.Select((p, i) => i == 0 ? p.TrimEnd('/', '\\') : p.Trim('/', '\\')));

        var candidates = new List<string> { Join(root, "runtime", platformDir, "bin", exe) };
        if (windows)
        {
            if (!string.IsNullOrEmpty(programFiles)) candidates.Add(Join(programFiles, "qemu", exe));
            candidates.Add(@"C:\Program Files\qemu\" + exe);
        }
        else if (os == OSPlatform.OSX)
        {
            candidates.Add("/opt/homebrew/bin/" + exe);   // Homebrew, Apple Silicon
            candidates.Add("/usr/local/bin/" + exe);      // Homebrew, Intel
            candidates.Add("/opt/local/bin/" + exe);      // MacPorts
        }

        foreach (string c in candidates)
            if (exists(c)) return c;

        foreach (string dir in (pathVar ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            string c = Join(dir.Trim('"'), exe);
            if (exists(c)) return c;
        }

        if (os == OSPlatform.Linux && exists("/usr/bin/" + exe)) return "/usr/bin/" + exe;
        return null;
    }

    /// <summary>找不到时给玩家看的话，按平台说清楚怎么装。</summary>
    public static string NotFoundMessage(string root) =>
        $"找不到 qemu-system-x86_64。" + (CurrentOs() == OSPlatform.Windows
            ? @"装 QEMU（https://qemu.weilnetz.de/w64/）到默认位置 C:\Program Files\qemu，"
            : CurrentOs() == OSPlatform.OSX ? "brew install qemu，" : "用包管理器装 qemu-system-x86，")
        + $"或放到 {Path.Combine(root, "runtime", PlatformDir, "bin")}，"
        + $"或用环境变量 {EnvVar} 指定完整路径。";

    public static string DescribePlatform(OSPlatform os, bool arm64) =>
        os == OSPlatform.Windows ? "windows-x86_64"
        : os == OSPlatform.OSX ? (arm64 ? "macos-arm64" : "macos-x86_64")
        : "linux-x86_64";

    private static OSPlatform CurrentOs() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;
}
