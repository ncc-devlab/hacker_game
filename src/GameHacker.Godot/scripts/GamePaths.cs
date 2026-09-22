using System.IO;
using GameHacker.Core.Qemu;
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
    /// 本平台的 QEMU，查找顺序见 <see cref="QemuLocator"/>。找不到时抛出的异常
    /// 会原样显示在状态栏上，所以消息里要写清楚怎么装。
    /// </summary>
    public static string QemuPath =>
        QemuLocator.Find(Root) ?? throw new FileNotFoundException(QemuLocator.NotFoundMessage(Root));

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
