using System.Net;
using PacketDotNet;

namespace GameHacker.Core.Net;

/// <summary>
/// 给关卡判定用的帧解析。和 <see cref="PacketLog"/> 的摘要分开：
/// 摘要是给玩家看的中文，判定不能去匹配那串文字。
/// </summary>
public static class PacketInspector
{
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
