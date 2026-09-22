using System.Net;
using PacketDotNet;

namespace GameHacker.Core.Net;

/// <summary>
/// 给关卡判定用的帧解析。和 <see cref="PacketLog"/> 的摘要分开：
/// 摘要是给玩家看的中文，判定不能去匹配那串文字。
/// </summary>
public static class PacketInspector
{
    /// <summary>这一帧是不是 <paramref name="from"/> 回给 <paramref name="to"/> 的 ICMP echo 应答。</summary>
    public static bool IsIcmpEchoReply(byte[] frame, IPAddress from, IPAddress to)
    {
        try
        {
            return Packet.ParsePacket(LinkLayers.Ethernet, frame) is EthernetPacket
            {
                PayloadPacket: IPv4Packet { PayloadPacket: IcmpV4Packet icmp } ip,
            }
            && icmp.TypeCode == IcmpV4TypeCode.EchoReply
            && ip.SourceAddress.Equals(from)
            && ip.DestinationAddress.Equals(to);
        }
        catch (Exception)
        {
            // 畸形帧是玩家能造出来的，判定器不能因此抛异常
            return false;
        }
    }
}
