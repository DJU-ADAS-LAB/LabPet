using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LabPet.Client.Network;

/// <summary>
/// VPN·Docker·WSL·Hyper-V 등 가상 어댑터를 제외하고 활성 물리 LAN/Wi-Fi 서브넷 브로드캐스트 주소를 계산한다.
/// </summary>
public static class LanNetworkHelper
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "vpn", "wireguard", "tailscale", "zerotier", "openvpn", "tap-windows", "wintun",
        "docker", "wsl", "vethernet", "hyper-v", "virtual", "vmware", "virtualbox",
        "vbox", "hamachi", "radmin", "loopback", "teredo", "isatap", "bluetooth"
    ];

    /// <summary>
    /// 어댑터 이름/설명/유형을 검사해 실제 물리 LAN 또는 Wi-Fi 어댑터인지 판별한다.
    /// </summary>
    public static bool IsPhysicalLanAdapter(
        NetworkInterfaceType interfaceType,
        OperationalStatus status,
        string name,
        string description)
    {
        if (status != OperationalStatus.Up)
        {
            return false;
        }

        if (interfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
        {
            return false;
        }

        var combined = $"{name} {description}".ToLowerInvariant();
        foreach (var keyword in VirtualAdapterKeywords)
        {
            if (combined.Contains(keyword, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// IPv4 주소와 서브넷 마스크로부터 해당 서브넷의 브로드캐스트 주소를 계산한다.
    /// </summary>
    public static IPAddress ComputeSubnetBroadcastAddress(IPAddress ipv4Address, IPAddress subnetMask)
    {
        var ipBytes = ipv4Address.GetAddressBytes();
        var maskBytes = subnetMask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4)
        {
            throw new ArgumentException("IPv4 주소와 서브넷 마스크만 지원합니다.");
        }

        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcast);
    }

    /// <summary>
    /// 현재 머신의 활성 물리 LAN/Wi-Fi 어댑터들에서 계산된 서브넷 브로드캐스트 주소 목록을 반환한다.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetPhysicalBroadcastAddresses()
    {
        var results = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysicalLanAdapter(nic.NetworkInterfaceType, nic.OperationalStatus, nic.Name, nic.Description))
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    results.Add(ComputeSubnetBroadcastAddress(unicast.Address, unicast.IPv4Mask));
                }
            }
        }
        catch
        {
            // 네트워크 인터페이스 열거 실패 시 안전 폴백
        }

        return results.ToList();
    }
}
