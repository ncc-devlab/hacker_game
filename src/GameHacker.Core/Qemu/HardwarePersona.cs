namespace GameHacker.Core.Qemu;

/// <summary>
/// 一台客户机对玩家宣称的身份：主板、BIOS、CPU、内核版本。
/// </summary>
/// <remarks>
/// <para><b>为什么需要。</b> 默认配置下客户机到处都写着"我是虚拟机"：
/// <c>uname -r</c> 是 <c>…-virt</c>，<c>/sys/class/dmi/id/sys_vendor</c> 是 QEMU，
/// BIOS 是 SeaBIOS，CPU 型号是 <c>QEMU Virtual CPU version 2.5+</c>，
/// 网卡 PCI ID 是 <c>0x1af4</c>（Red Hat virtio）。玩家在游戏里"入侵"的
/// 每一台机器都长这样，沉浸感直接没了。</para>
/// <para><b>分两层实现。</b> 硬件那一层在宿主侧用 QEMU 命令行做掉
/// （<c>-smbios</c> / <c>-cpu</c>），这是 QEMU 原生支持的参数，不需要魔改；
/// 内核与发行版那一层在客户机 init 里做（见 <c>m0/guest/common.sh</c> 的
/// <c>m0_disguise</c>），靠 bind-mount 覆盖 <c>/proc/version</c>、
/// <c>/proc/cmdline</c> 并替换 <c>/bin/uname</c>。</para>
/// <para><b>能挡到什么程度。</b> 挡得住玩家在游戏里会做的所有常规侦察：
/// <c>uname -a</c>、<c>/proc/version</c>、<c>/proc/cmdline</c>、
/// <c>/proc/cpuinfo</c>、<c>dmidecode</c>、<c>/sys/class/dmi</c>、<c>dmesg</c>。
/// 挡不住的是 <c>busybox uname</c> 这种绕开 PATH 直接调 applet 的写法，
/// 以及 CPUID 的 hypervisor 位 —— 那两样只有换成自建内核才能根治，
/// 见 <c>docs/伪装.md</c>。</para>
/// </remarks>
public sealed record HardwarePersona
{
    /// <summary>预设的名字，只用于日志。</summary>
    public required string Name { get; init; }

    // --- 宿主侧：QEMU 命令行 -------------------------------------------------

    /// <summary><c>-cpu</c> 的型号。用具名型号而不是默认的 <c>qemu64</c>。</summary>
    public string CpuModel { get; init; } = "IvyBridge";

    /// <summary>
    /// 覆盖 <c>/proc/cpuinfo</c> 的 <c>model name</c>。
    /// </summary>
    /// <remarks>
    /// 具名型号自带的名字是服务器 CPU（IvyBridge 报的是
    /// "Intel Xeon E3-12xx v2"），装在台式机人设上很突兀，所以再覆一层。
    /// </remarks>
    public string CpuModelId { get; init; } = "Intel(R) Core(TM) i5-3470 CPU @ 3.20GHz";

    public string BiosVendor { get; init; } = "Dell Inc.";
    public string BiosVersion { get; init; } = "A29";
    public string BiosDate { get; init; } = "03/12/2018";

    public string SystemManufacturer { get; init; } = "Dell Inc.";
    public string SystemProduct { get; init; } = "OptiPlex 7010";
    public string SystemVersion { get; init; } = "01";
    public string SystemSerial { get; init; } = "7QK2LZ1";
    public string SystemFamily { get; init; } = "OptiPlex";

    public string BoardManufacturer { get; init; } = "Dell Inc.";
    public string BoardProduct { get; init; } = "0773VG";

    public string ChassisManufacturer { get; init; } = "Dell Inc.";

    /// <summary>SATA 盘的型号与序列号，<c>hdparm</c> / <c>lsblk -o MODEL</c> 看得到。</summary>
    public string DiskModel { get; init; } = "ST500DM002-1BD142";
    public string DiskSerial { get; init; } = "W2AYF3K1";

    // --- 客户机侧：经内核 cmdline 传进去，再由 init 落地 ---------------------

    /// <summary>
    /// 内核版本后缀。客户机把真实的 <c>…-virt</c> 换成 <c>…-{flavor}</c>。
    /// </summary>
    /// <remarks>
    /// 不写死完整版本号是有意的：写死的话每次升级客户机内核都要跟着改一遍，
    /// 迟早和 <c>/proc/version</c>、<c>/lib/modules</c> 对不上，
    /// 那种"版本号互相打架"的破绽比 <c>-virt</c> 本身还刺眼。
    /// </remarks>
    public string KernelFlavor { get; init; } = "lts";

    /// <summary>
    /// 完整覆盖 <c>uname -r</c>。留空则走 <see cref="KernelFlavor"/> 的后缀替换。
    /// </summary>
    public string KernelRelease { get; init; } = "";

    /// <summary><c>uname -v</c>。留空则保留客户机真实值。</summary>
    public string KernelVersion { get; init; } = "";

    /// <summary>
    /// 假的 <c>/proc/cmdline</c>。
    /// </summary>
    /// <remarks>
    /// 非改不可：真实 cmdline 里有 <c>console=ttyS0</c> 和一整排 <c>m0.*</c>，
    /// 玩家 <c>cat /proc/cmdline</c> 就把游戏的内部协议全看完了。
    /// </remarks>
    public string FakeCmdline { get; init; } =
        "BOOT_IMAGE=/boot/vmlinuz root=UUID=3f2a91c4-7d18-4e63-9a05-b61c8f2d7e40 ro quiet";

    /// <summary>
    /// 改写 <c>/etc/os-release</c>。留空表示不动。
    /// </summary>
    /// <remarks>
    /// 默认留空是有理由的：客户机是货真价实的 Alpine + busybox，
    /// 硬说自己是 Ubuntu 的话 <c>/etc/apk</c> 还在、<c>dpkg</c> 又没有，
    /// 处处对不上，比不伪装还假。要做架空发行版时再填。
    /// </remarks>
    public string OsPrettyName { get; init; } = "";
    public string OsId { get; init; } = "";
    public string OsVersionId { get; init; } = "";

    // --- 预设 ---------------------------------------------------------------

    /// <summary>玩家自己的机器：一台还在服役的办公台式机。</summary>
    public static HardwarePersona Workstation { get; } = new() { Name = "workstation" };

    /// <summary>
    /// 目标机：机房角落里吃灰的 1U 小服务器，对应概要书里"遍地的老旧小机器"。
    /// </summary>
    public static HardwarePersona LegacyServer { get; } = new()
    {
        Name = "legacy-server",
        DiskModel = "WDC WD2503ABYX-01WERA1",
        DiskSerial = "WD-WMAYP0942816",
        CpuModel = "Westmere",
        CpuModelId = "Intel(R) Xeon(R) CPU E5620 @ 2.40GHz",
        BiosVendor = "American Megatrends Inc.",
        BiosVersion = "2.0b",
        BiosDate = "08/26/2014",
        SystemManufacturer = "Supermicro",
        SystemProduct = "X8DTU",
        SystemVersion = "0123456789",
        SystemSerial = "0123456789",
        SystemFamily = "To be filled by O.E.M.",
        BoardManufacturer = "Supermicro",
        BoardProduct = "X8DTU",
        ChassisManufacturer = "Supermicro",
        // 机房里的机器习惯上留着串口控制台，这条反而更像真的
        FakeCmdline = "BOOT_IMAGE=/boot/vmlinuz root=UUID=8f3a1c6e-2d4b-4f19-9a77-31c05be6d0aa ro console=ttyS0,115200 quiet",
    };

    /// <summary>不伪装。M0/M1 的老测试和排查时用。</summary>
    public static HardwarePersona None { get; } = new() { Name = "none" };

    /// <summary>
    /// 组装 <c>-smbios</c> / <c>-cpu</c> 参数。
    /// </summary>
    /// <remarks>
    /// SMBIOS 字段里逗号是分隔符，必须转义成 <c>,,</c>，
    /// 否则 "To be filled by O.E.M., Inc." 这种值会被截断成半句。
    /// </remarks>
    public IEnumerable<string> QemuArguments()
    {
        if (ReferenceEquals(this, None)) yield break;

        yield return "-cpu";
        yield return $"{CpuModel},model-id={Escape(CpuModelId)}";

        yield return "-smbios";
        yield return $"type=0,vendor={Escape(BiosVendor)},version={Escape(BiosVersion)}," +
                     $"date={Escape(BiosDate)},uefi=off";

        yield return "-smbios";
        yield return $"type=1,manufacturer={Escape(SystemManufacturer)},product={Escape(SystemProduct)}," +
                     $"version={Escape(SystemVersion)},serial={Escape(SystemSerial)}," +
                     $"family={Escape(SystemFamily)}";

        yield return "-smbios";
        yield return $"type=2,manufacturer={Escape(BoardManufacturer)},product={Escape(BoardProduct)}";

        yield return "-smbios";
        yield return $"type=3,manufacturer={Escape(ChassisManufacturer)}";
    }

    /// <summary>
    /// 经内核 cmdline 传给客户机 init 的部分。
    /// </summary>
    /// <remarks>
    /// 内核 cmdline 以空格分词，值里不能有空格，所以用 <c>~</c> 代替空格，
    /// 客户机侧再还原（见 <c>m0_unesc</c>）。这些参数本身也是穿帮点，
    /// 但 init 会在启动末尾把 <c>/proc/cmdline</c> bind-mount 掉，玩家看不到。
    /// </remarks>
    public string KernelCmdlineFragment()
    {
        if (ReferenceEquals(this, None)) return "";
        var parts = new List<string>();
        Add("m0.uts_flavor", KernelFlavor);
        Add("m0.uts_r", KernelRelease);
        Add("m0.uts_v", KernelVersion);
        Add("m0.cmdline", FakeCmdline);
        Add("m0.os_name", OsPrettyName);
        Add("m0.os_id", OsId);
        Add("m0.os_ver", OsVersionId);
        return parts.Count == 0 ? "" : " " + string.Join(' ', parts);

        void Add(string key, string value)
        {
            // 空值不能发：客户机那边 `m0.uts_r=` 取出来是空串，
            // 会被当成"给了但为空"，把后缀替换那条路堵掉
            if (!string.IsNullOrEmpty(value)) parts.Add($"{key}={Space(value)}");
        }
    }

    /// <summary>挂 <c>ide-hd</c> 时附加的型号与序列号。</summary>
    public string DiskDeviceSuffix() =>
        ReferenceEquals(this, None)
            ? ""
            : $",model={Escape(DiskModel)},serial={Escape(DiskSerial)}";

    private static string Escape(string value) => value.Replace(",", ",,");

    private static string Space(string value) => value.Replace(" ", "~");
}
