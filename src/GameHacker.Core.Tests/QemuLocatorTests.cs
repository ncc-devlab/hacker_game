using System.Runtime.InteropServices;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

public class QemuLocatorTests
{
    private static string? Find(OSPlatform os, string? env, string? path, params string[] existing)
    {
        var set = new HashSet<string>(existing);
        return QemuLocator.Find("/game", os, QemuLocator.DescribePlatform(os, arm64: true),
                                env, path, @"C:\Program Files", set.Contains);
    }

    [Fact]
    public void EnvironmentVariableWinsEvenIfMissing()
    {
        Assert.Equal("/x/qemu", Find(OSPlatform.Linux, "/x/qemu", "/usr/bin", "/usr/bin/qemu-system-x86_64"));
    }

    [Fact]
    public void BundledRuntimeBeatsSystemInstall()
    {
        string bundled = Path.Combine("/game", "runtime", "macos-arm64", "bin", "qemu-system-x86_64");
        Assert.Equal(bundled, Find(OSPlatform.OSX, null, "", bundled, "/opt/homebrew/bin/qemu-system-x86_64"));
    }

    [Fact]
    public void WindowsFindsDefaultInstallWithoutPath()
    {
        // 官方安装包不改 PATH —— 第二轮 Windows 实测就是这样
        string exe = Path.Combine(@"C:\Program Files", "qemu", "qemu-system-x86_64.exe");
        Assert.Equal(exe, Find(OSPlatform.Windows, null, @"C:\Windows\system32", exe));
    }

    [Fact]
    public void MacFindsHomebrewWithFinderPath()
    {
        // Finder 启动的程序 PATH 只有系统目录
        Assert.Equal("/opt/homebrew/bin/qemu-system-x86_64",
            Find(OSPlatform.OSX, null, "/usr/bin:/bin:/usr/sbin:/sbin", "/opt/homebrew/bin/qemu-system-x86_64"));
    }

    [Fact]
    public void FallsBackToPathThenNull()
    {
        Assert.Equal(Path.Combine("/snap/bin", "qemu-system-x86_64"),
            Find(OSPlatform.Linux, null, "/usr/local/bin:/snap/bin", "/snap/bin/qemu-system-x86_64"));
        Assert.Null(Find(OSPlatform.Linux, null, "/usr/local/bin"));
    }
}
