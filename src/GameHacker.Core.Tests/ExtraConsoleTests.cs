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
}
