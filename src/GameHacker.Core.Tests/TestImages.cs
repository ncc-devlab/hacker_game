
namespace GameHacker.Core.Tests;

/// <summary>
/// 定位 M0 构建出来的客户机镜像与裁剪版 QEMU。
/// </summary>
/// <remarks>
/// 镜像是构建产物、不入库（见 .gitignore），所以集成测试在缺镜像时应当
/// 明确跳过而不是失败 —— 失败会把「环境没准备好」和「代码坏了」混为一谈。
/// </remarks>
public static class TestImages
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ImagesDir => Path.Combine(RepoRoot, "m0", "images");
    public static string Kernel => Path.Combine(ImagesDir, "vmlinuz-virt");
    public static string Initrd => Path.Combine(ImagesDir, "m0-guest.cpio.gz");
    public static string AlpineDisk => Path.Combine(ImagesDir, "alpine-main.qcow2");

    /// <summary>
    /// 用哪个 QEMU：和游戏同一套查找逻辑（<see cref="Qemu.QemuLocator"/>）。
    /// 找不到时返回裸文件名，让启动报错说清楚。
    /// </summary>
    /// <remarks>
    /// 三端 CI 靠 <c>GAMEHACKER_QEMU</c> 指向各自平台的裁剪版二进制。
    /// </remarks>
    public static string QemuPath =>
        Qemu.QemuLocator.Find(RepoRoot) ?? "qemu-system-x86_64";

    public static bool GuestImagesReady => File.Exists(Kernel) && File.Exists(Initrd);

    public const string MissingImagesReason =
        "缺少 M0 客户机镜像。先跑 m0/scripts/00-fetch-images.sh 与 01-build-guest-initramfs.sh。";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "m0", "scripts")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录（向上找不到 m0/scripts）");
    }
}
