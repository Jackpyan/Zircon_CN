using System.Net;
using Server.Envir;

namespace Server.Web.Services;

public static class AdminEndpointValidator
{
    /// <summary>
    /// 是否来自内网/本机（主密码应急登录只允许内网使用）。
    /// 认可 127.0.0.0/8、10/8、172.16/12、192.168/16、::1 与 IPv6 链路/站点本地。
    /// </summary>
    public static bool IsIntranet(IPAddress? address)
    {
        if (address == null) return false;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;

        byte[] bytes = address.GetAddressBytes();

        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || bytes[0] == 127;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);
    }

    public static IPAddress ValidateAndResolve()
    {
        if (!Config.AdminWebEnabled)
            throw new InvalidOperationException("Server.Web 已禁用。请在 Server.ini 的 [AdminWeb] 中设置 AdminWebEnabled=True。");
        if (string.IsNullOrWhiteSpace(Config.AdminUserName))
            throw new InvalidOperationException("[AdminWeb] AdminUserName 不能为空。");
        if (string.IsNullOrEmpty(Config.MasterPassword))
            throw new InvalidOperationException("[System] MasterPassword 不能为空。");
        if (Config.AdminWebPort == 0)
            throw new InvalidOperationException("[AdminWeb] AdminWebPort 必须在 1 到 65535 之间。");

        HashSet<int> reservedPorts = [Config.Port, Config.UserCountPort];
        AddListenerPort(Config.WebPrefix, reservedPorts);
        AddListenerPort(Config.BuyPrefix, reservedPorts);
        AddListenerPort(Config.IPNPrefix, reservedPorts);

        if (reservedPorts.Contains(Config.AdminWebPort))
            throw new InvalidOperationException($"管理端口 {Config.AdminWebPort} 与现有服务器监听端口冲突。");

        if (string.Equals(Config.AdminWebHost, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (!IPAddress.TryParse(Config.AdminWebHost, out IPAddress? address))
            throw new InvalidOperationException($"AdminWebHost '{Config.AdminWebHost}' 不是有效的 IP 地址。");

        return address;
    }

    private static void AddListenerPort(string? value, ISet<int> ports)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        string normalized = value.Replace("*", "127.0.0.1", StringComparison.Ordinal);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri))
            ports.Add(uri.Port);
    }
}
