using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenShare.Core;

/// <summary>本机局域网 IPv4 地址枚举，过滤回环、虚拟隧道与无效地址。</summary>
public static class NetworkUtils
{
    public readonly record struct NicAddress(string NicName, string Description, IPAddress Address)
    {
        public override string ToString() => $"{NicName}（{Description}）— {Address}";
    }

    /// <summary>返回所有可用的局域网 IPv4 地址（排除回环 127.*、自动配置 169.254.*、隧道与未启用网卡）。</summary>
    public static List<NicAddress> GetLocalIPv4Addresses()
    {
        var result = new List<NicAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip.Address)) continue;
                // 169.254.x.x 是 DHCP 失败时的自动配置地址，不用于局域网教学
                if (ip.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                result.Add(new NicAddress(nic.Name, nic.Description, ip.Address));
            }
        }
        return result;
    }
}
