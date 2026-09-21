using System.IO;
using Godot;

namespace GameHacker.Godot;

/// <summary>
/// 定位 QEMU 二进制与客户机镜像。
/// </summary>
/// <remarks>
/// <para>
/// <b>这些东西不能放进 <c>res://</c>。</b> 导出后 <c>res://</c> 会被打包成 .pck，
/// 里面的文件既不能执行也不能写。QEMU 二进制要能 exec，磁盘镜像要能写，
/// 所以它们必须是可执行文件旁边的普通外部目录。
/// </para>
/// <para>
/// 编辑器里跑的时候按仓库布局找（<c>m0/images</c>、<c>runtime/</c>）；
/// 导出后按可执行文件同级目录找。Steam 按平台分 depot，每端只带自己那份 runtime。
/// </para>
/// </remarks>
public static class GamePaths
{
    /// <summary>
    /// 外部资源根目录：编辑器下是仓库根，导出后是可执行文件所在目录。
    /// </summary>
    public static string Root { get; } = Resolve();

    public static string ImagesDir => Path.Combine(Root, "m0", "images");
    public static string Kernel => Path.Combine(ImagesDir, "vmlinuz-virt");
    public static string Initrd => Path.Combine(ImagesDir, "m0-guest.cpio.gz");
    public static string AlpineDisk => Path.Combine(ImagesDir, "alpine-main.qcow2");

    /// <summary>
    /// 本平台的 QEMU。优先环境变量，其次 M0-e 裁剪出来的那份，最后退回 PATH。
    /// </summary>
    public static string QemuPath
    {
        get
        {
            string? fromEnv = System.Environment.GetEnvironmentVariable("GAMEHACKER_QEMU");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

            string exe = OS.GetName() switch
            {
                "Windows" => "qemu-system-x86_64.exe",
                _ => "qemu-system-x86_64",
            };
            string platform = OS.GetName() switch
            {
                "Windows" => "windows-x86_64",
                "macOS" => "macos-" + (Engine.GetArchitectureName() == "arm64" ? "arm64" : "x86_64"),
                _ => "linux-x86_64",
            };

            string bundled = Path.Combine(Root, "runtime", platform, "bin", exe);
            return File.Exists(bundled) ? bundled : exe;
        }
    }

    public static bool ImagesReady => File.Exists(Kernel) && File.Exists(Initrd);

    private static string Resolve()
    {
        if (OS.HasFeature("editor"))
        {
            // 编辑器里：res:// 就是 src/GameHacker.Godot，往上两级是仓库根
            string projectDir = ProjectSettings.GlobalizePath("res://");
            return Path.GetFullPath(Path.Combine(projectDir, "..", ".."));
        }
        return Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".";
    }
}
