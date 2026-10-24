using Mirror;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace LightReflectiveMirror
{
    public partial class Program
    {
        public static WebClient webClient = new();
        public static Transport transport;
        public static Program instance;
        public static Config conf;
        
        private RelayHandler _relay;
        private MethodInfo _awakeMethod;
        private MethodInfo _startMethod;
        private MethodInfo _updateMethod;
        private MethodInfo _lateUpdateMethod;

        private DateTime _startupTime;
        public static string publicIP;
        private List<int> _currentConnections = new();
        public Dictionary<int, IPEndPoint> NATConnections = new();
        private BiDictionary<int, string> _pendingNATPunches = new();
        // filled by the NAT punch thread, drained on the main loop: the dictionaries above are not thread safe
        private ConcurrentQueue<(string natID, IPEndPoint endpoint)> _receivedNATPunches = new();

        private Dictionary<int, string> _connectionAddresses = new();
        private Dictionary<string, int> _connectionsPerAddress = new();
        private int _currentHeartbeatTimer = 0;

        private byte[] _NATRequest = new byte[500];
        private int _NATRequestPosition = 0;

        private UdpClient _punchServer;

        private readonly string CONFIG_PATH = System.Environment.GetEnvironmentVariable("LRM_CONFIG_PATH") ?? "config.json";
    }

    public enum LRMRegions { Any, NorthAmerica, SouthAmerica, Europe, Asia, Africa, Oceania }
}
