using System.Linq;
using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>
/// 伪装参数的拼装规则。
/// </summary>
/// <remarks>
/// 这几条都是"错了不会报错、只会静悄悄少伪装一半"的地方：
/// SMBIOS 的逗号没转义就被截断、cmdline 的空格没换掉就被内核切成两个参数。
/// 客户机侧不会抱怨，只有玩家 <c>cat /proc/version</c> 时才看得出来。
/// </remarks>
public class HardwarePersonaTests
{
    [Fact]
    public void 不伪装时不产生任何_QEMU_参数()
    {
        Assert.Empty(HardwarePersona.None.QemuArguments());
        Assert.Equal("", HardwarePersona.None.KernelCmdlineFragment());
        Assert.Equal("", HardwarePersona.None.DiskDeviceSuffix());
    }

    [Fact]
    public void SMBIOS_值里的逗号被转义成双逗号()
    {
        // 逗号是 -smbios 的字段分隔符。"To be filled by O.E.M." 这类真实厂商串
        // 里带逗号的很常见，不转义的话整条 type=1 会从那里断掉。
        var persona = HardwarePersona.Workstation with
        {
            Name = "t",
            SystemManufacturer = "Acme, Inc.",
        };
        string type1 = persona.QemuArguments()
            .First(a => a.StartsWith("type=1,"));

        Assert.Contains("manufacturer=Acme,, Inc.", type1);
    }

    [Fact]
    public void 内核_cmdline_片段里没有空格()
    {
        // 内核按空格分词。值里留一个空格，后半截就变成另一个独立参数，
        // 客户机取到的 m0.cmdline 只有前半句。
        string fragment = HardwarePersona.Workstation.KernelCmdlineFragment();

        Assert.NotEqual("", fragment);
        foreach (string token in fragment.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            Assert.StartsWith("m0.", token);
    }

    [Fact]
    public void 留空的字段不进_cmdline()
    {
        // 发 m0.uts_r= 的话客户机那边取出来是空串，会被当成"给了但为空"，
        // 把按后缀派生版本号那条路堵死。
        string fragment = HardwarePersona.Workstation.KernelCmdlineFragment();

        Assert.DoesNotContain("m0.uts_r=", fragment);
        Assert.DoesNotContain("m0.os_name=", fragment);
        Assert.Contains("m0.uts_flavor=lts", fragment);
    }

    [Fact]
    public void 带人设的命令行里不再有_virtio_与_QEMU_字样()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        var spec = new VmSpec
        {
            Name = "web01",
            KernelPath = "/k",
            InitrdPath = "/i",
            DiskPath = "/d.qcow2",
            SwitchPort = 1234,
            Persona = HardwarePersona.Workstation,
        };

        string line = string.Join(' ', launcher.BuildArguments(spec, 1, 2, 3));

        Assert.DoesNotContain("virtio", line);
        Assert.Contains("e1000e", line);
        Assert.Contains("ide-hd", line);
        Assert.Contains("m0.root=/dev/sda", line);
        Assert.Contains("-smbios", line);
        Assert.Contains("model-id=", line);
    }
}
