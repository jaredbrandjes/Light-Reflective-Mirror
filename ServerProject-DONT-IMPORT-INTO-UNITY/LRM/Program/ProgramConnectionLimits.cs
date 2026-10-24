using System.Net;

namespace LightReflectiveMirror
{
    partial class Program
    {
        /// <summary>
        /// Counts a new connection against its IP. Returns false if that IP is already at MaxConnectionsPerIP.
        /// </summary>
        bool TryAddConnectionAddress(int clientID)
        {
            if (conf.MaxConnectionsPerIP <= 0)
                return true;

            string address;
            try
            {
                address = GetAddressKey(transport.ServerGetClientAddress(clientID));
            }
            catch
            {
                // A throw here would skip registering the client, leaving a connection
                // the relay ignores but never times out. Let it through uncounted instead.
                return true;
            }

            // can't tell who it is, don't hold it against anyone
            if (string.IsNullOrEmpty(address))
                return true;

            _connectionsPerAddress.TryGetValue(address, out int count);

            if (count >= conf.MaxConnectionsPerIP)
                return false;

            _connectionsPerAddress[address] = count + 1;
            _connectionAddresses[clientID] = address;
            return true;
        }

        void RemoveConnectionAddress(int clientID)
        {
            if (!_connectionAddresses.Remove(clientID, out string address))
                return;

            if (--_connectionsPerAddress[address] <= 0)
                _connectionsPerAddress.Remove(address);
        }

        /// <summary>
        /// KCP reports a bare IP, SimpleWeb reports "ip:port". Count both by IP only.
        /// </summary>
        static string GetAddressKey(string address)
        {
            if (!IPEndPoint.TryParse(address ?? "", out IPEndPoint endPoint))
                return address;

            IPAddress ip = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
            return ip.ToString();
        }
    }
}
