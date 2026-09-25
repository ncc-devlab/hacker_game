using GameHacker.Core.Qemu;

namespace GameHacker.Core.Tests;

/// <summary>玩家机上多开的终端：落在哪几个串口上、命令行怎么接。</summary>
public class ExtraConsoleTests
{
    private static VmSpec Spec(int extra, AdminAccount? admin = null) => new()
    {
        Name = "ws",
        KernelPath = "/k",
        InitrdPath = "/i",
        Nics = [new VmNic("52:54:00:00:01:00", 1234)],
        Admin = admin,
        ExtraConsoles = extra,
    };

    [Fact]
    public void 没有管理员时占_ttyS2_和_ttyS3()
    {
        Assert.Equal(["ttyS2", "ttyS3"], QemuLauncher.ExtraConsoleTtys(hasAdmin: false, wanted: 2));
    }

    [Fact]
    public void 管理员占着_ttyS2_就只剩_ttyS3()
    {
        Assert.Equal(["ttyS3"], QemuLauncher.ExtraConsoleTtys(hasAdmin: true, wanted: 2));
    }

    [Fact]
    public void 串口不够时不超出四个()
    {
        Assert.Equal(2, QemuLauncher.ExtraConsoleTtys(hasAdmin: false, wanted: 9).Count);
        Assert.Empty(QemuLauncher.ExtraConsoleTtys(hasAdmin: false, wanted: 0));
    }

    [Fact]
    public void 命令行里接上串口并告诉客户机是哪几个()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        string line = string.Join(' ', launcher.BuildArguments(Spec(2), 1, 2, 3, extraPorts: [40, 41]));

        Assert.Contains("m0.consoles=ttyS2,ttyS3", line);
        Assert.Contains("id=x0,host=127.0.0.1,port=40", line);
        Assert.Contains("id=x1,host=127.0.0.1,port=41", line);
        Assert.Contains("chardev:x1", line);
        // 真的 ISA 串口，不是 virtio：lspci 里不能多出 Red Hat 的设备
        Assert.DoesNotContain("virtio", line);
    }

    [Fact]
    public void 管理员的串口排在多开终端前面()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        var args = launcher.BuildArguments(Spec(2, new AdminAccount("opsadm", "pw")), 1, 2, 3, adminPort: 9,
                                           extraPorts: [40]);
        int admin = args.ToList().IndexOf("chardev:adm");
        int extra = args.ToList().IndexOf("chardev:x0");
        Assert.True(admin >= 0 && extra > admin, "-serial 按出现顺序编号，管理员必须是 ttyS2");
        Assert.Contains(" m0.consoles=ttyS3", string.Join(' ', args));
    }

    [Fact]
    public void 端口数和串口数对不上就报错()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        Assert.Throws<ArgumentException>(() => launcher.BuildArguments(Spec(2), 1, 2, 3));
    }

    // --- 「神器」blackwall 接入口 -------------------------------------------

    private static VmSpec Target(bool blackwall, AdminAccount? admin = null) => new()
    {
        Name = "backup",
        KernelPath = "/k",
        InitrdPath = "/i",
        Nics = [new VmNic("52:54:00:00:02:00", 2345)],
        Admin = admin,
        Blackwall = blackwall,
    };

    [Fact]
    public void blackwall_没管理员时落在_ttyS2()
    {
        Assert.Equal("ttyS2", QemuLauncher.BlackwallTty(Target(blackwall: true)));
    }

    [Fact]
    public void blackwall_有管理员时排到_ttyS3()
    {
        Assert.Equal("ttyS3", QemuLauncher.BlackwallTty(Target(blackwall: true, admin: new AdminAccount("opsadm", "pw"))));
    }

    [Fact]
    public void 没开blackwall时没有接入口()
    {
        Assert.Null(QemuLauncher.BlackwallTty(Target(blackwall: false)));
    }

    [Fact]
    public void 串口用满时blackwall没有位置()
    {
        // 多开两个终端已经占掉 ttyS2/ttyS3，再要 blackwall 就没串口了
        var spec = Spec(2) with { Blackwall = true };
        Assert.Null(QemuLauncher.BlackwallTty(spec));
    }

    [Fact]
    public void blackwall_接上串口并进_m0_consoles()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        string line = string.Join(' ', launcher.BuildArguments(Target(blackwall: true), 1, 2, 3, blackwallPort: 50));

        Assert.Contains("m0.consoles=ttyS2", line);
        Assert.Contains("id=bw,host=127.0.0.1,port=50", line);
        Assert.Contains("chardev:bw", line);
        Assert.DoesNotContain("virtio", line);
    }

    [Fact]
    public void blackwall_排在多开终端之后()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        var args = launcher.BuildArguments(Spec(1) with { Blackwall = true }, 1, 2, 3,
                                           extraPorts: [40], blackwallPort: 50);
        int extra = args.ToList().IndexOf("chardev:x0");
        int bw = args.ToList().IndexOf("chardev:bw");
        Assert.True(extra >= 0 && bw > extra, "-serial 按出现顺序编号：blackwall 排在多开终端后面");
        // 多开终端占 ttyS2，blackwall 落到 ttyS3
        Assert.Contains("m0.consoles=ttyS2,ttyS3", string.Join(' ', args));
    }

    [Fact]
    public void 要开blackwall却没给端口就报错()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        Assert.Throws<ArgumentException>(() => launcher.BuildArguments(Target(blackwall: true), 1, 2, 3));
    }

    [Fact]
    public void blackwall命令只在需要时装()
    {
        var launcher = new QemuLauncher("qemu-system-x86_64");
        Assert.Contains(" m0.blackwall_tool=1",
            string.Join(' ', launcher.BuildArguments(Spec(0) with { BlackwallClient = true }, 1, 2, 3)));
        Assert.DoesNotContain("m0.blackwall_tool",
            string.Join(' ', launcher.BuildArguments(Spec(0), 1, 2, 3)));
    }
}
