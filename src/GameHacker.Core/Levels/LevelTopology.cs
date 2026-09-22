using System.Net;

namespace GameHacker.Core.Levels;

/// <summary>落到实处的一块网卡：接哪个 VLAN、什么 MAC、什么地址。</summary>
/// <param name="IpWithPrefix">如 10.0.0.2/24，直接交给客户机 init。</param>
public sealed record PlannedNic(string Network, int Vlan, string Mac, string IpWithPrefix);

/// <summary>
/// 把关卡里的网段和网卡定义换算成拉起虚拟机要用的参数。
/// </summary>
/// <remarks>只接受已经过 <see cref="LevelCatalog"/> 校验的关卡。</remarks>
public static class LevelTopology
{
    /// <summary>交换机上要开的 VLAN。</summary>
    public static IReadOnlyList<int> Vlans(LevelDefinition level) =>
        level.Networks.Select(n => n.Vlan).ToList();

    /// <summary>第 <paramref name="machineIndex"/> 台机器的网卡，顺序即 eth0、eth1……</summary>
    public static IReadOnlyList<PlannedNic> Nics(LevelDefinition level, int machineIndex)
    {
        var machine = level.Machines[machineIndex];
        return machine.Nics.Select((nic, i) =>
        {
            var network = level.Networks.Single(n => n.Name == nic.Network);
            int prefix = IPNetwork.Parse(network.Subnet).PrefixLength;
            // 52:54:00 是 QEMU 的 OUI；后三字节 = 00:机器序号:网卡序号，一关之内不会撞
            string mac = $"52:54:00:00:{machineIndex + 1:x2}:{i:x2}";
            return new PlannedNic(network.Name, network.Vlan, mac, $"{nic.Ip}/{prefix}");
        }).ToList();
    }
}
