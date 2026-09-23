using System.Net;
using PacketDotNet;

namespace GameHacker.Core.Net;

/// <summary>
/// 给关卡判定用的帧解析。和 <see cref="PacketLog"/> 的摘要分开：
/// 摘要是给玩家看的中文，判定不能去匹配那串文字。
/// </summary>
public static class PacketInspector
{
    /// <summary>这一帧是不是 IPv4；是的话给出源、目的地址。</summary>
    public static bool TryGetIpv4(byte[] frame, out IPAddress source, out IPAddress destination)
    {
        source = destination = IPAddress.None;
        try
        {
            if (Packet.ParsePacket(LinkLayers.Ethernet, frame) is not EthernetPacket { PayloadPacket: IPv4Packet ip })
                return false;
            source = ip.SourceAddress;
            destination = ip.DestinationAddress;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 这一帧是不是 ARP；是的话给出发问的人和被问的地址。
    /// </summary>
    /// <remarks>
    /// 扫描判定要靠它：扫一个网段时，绝大多数地址上根本没有机器，
    /// 试探止步于没人应答的 ARP 询问，一个 IP 包都不会产生。
    /// </remarks>
    public static bool TryGetArp(byte[] frame, out IPAddress sender, out IPAddress target)
    {
        sender = target = IPAddress.None;
        try
        {
            if (Packet.ParsePacket(LinkLayers.Ethernet, frame) is not EthernetPacket { PayloadPacket: ArpPacket arp })
                return false;
            sender = arp.SenderProtocolAddress;
            target = arp.TargetProtocolAddress;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>这一帧的 TCP / UDP 端口。不是这两种协议就返回 false。</summary>
    public static bool TryGetPorts(byte[] frame, out int sourcePort, out int destinationPort)
    {
        sourcePort = destinationPort = 0;
        try
        {
            if (Packet.ParsePacket(LinkLayers.Ethernet, frame) is not EthernetPacket { PayloadPacket: IPv4Packet ip })
                return false;
            switch (ip.PayloadPacket)
            {
                case TcpPacket tcp:
                    sourcePort = tcp.SourcePort;
                    destinationPort = tcp.DestinationPort;
                    return true;
                case UdpPacket udp:
                    sourcePort = udp.SourcePort;
                    destinationPort = udp.DestinationPort;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>这一帧是不是 ICMP echo 应答；是的话给出源、目的地址。</summary>
    public static bool TryGetIcmpEchoReply(byte[] frame, out IPAddress source, out IPAddress destination)
    {
        source = destination = IPAddress.None;
        try
        {
            if (Packet.ParsePacket(LinkLayers.Ethernet, frame) is not EthernetPacket
                {
                    PayloadPacket: IPv4Packet { PayloadPacket: IcmpV4Packet icmp } ip,
                }
                || icmp.TypeCode != IcmpV4TypeCode.EchoReply)
                return false;
            source = ip.SourceAddress;
            destination = ip.DestinationAddress;
            return true;
        }
        catch (Exception)
        {
            // 畸形帧是玩家能造出来的，判定器不能因此抛异常
            return false;
        }
    }
}
