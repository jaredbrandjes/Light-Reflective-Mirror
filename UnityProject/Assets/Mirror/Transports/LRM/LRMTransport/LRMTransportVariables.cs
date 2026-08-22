using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Mirror;
using UnityEngine.Events;

namespace LightReflectiveMirror {
    public partial class LightReflectiveMirrorTransport : Transport {
        // Connection/auth variables
        public Transport clientToServerTransport;
        public string serverIP = null;
        public ushort serverPort = 7777;
        public ushort endpointServerPort = 8080;
        public float heartBeatInterval = 3;
        public bool connectOnAwake = true;
        public string authenticationKey = "Secret Auth Key";

        public UnityEvent disconnectedFromRelay;
        public UnityEvent connectedToRelay;

        // NAT Puncher variables
        public bool useNATPunch = false;
        public int NATPunchtroughPort = -1;
        private const int NAT_PUNCH_ATTEMPTS = 3;

        // LLB variables (LRM Load Balancer)
        public bool useLoadBalancer = false;
        public ushort loadBalancerPort = 7070;
        public string loadBalancerAddress = null;

        // Server hosting variables
        public string serverName = "My awesome server!";
        public string extraServerData = "Map 1";
        public int maxServerPlayers = 10;
        public bool isPublicServer = true;

        private const string LOCALHOST = "127.0.0.1";

        // Server list variables
        public UnityEvent serverListUpdated;
        public List<Room> relayServerList { private set; get; } = new List<Room> ();

        // Current Server Information
        public string serverStatus = "Not Started.";
        public string serverId = string.Empty;
        public int appId = -1;

        private LRMDirectConnectModule _directConnectModule;

        public LRMRegions region = LRMRegions.NorthAmerica;
        private byte[] _clientSendBuffer;
        private bool _connectedToRelay = false;
        private bool _isClient = false;

        public bool IsClient {
            get => _isClient;
            set => _isClient = value;
        }
        private bool _isServer = false;

        public bool IsServer {
            get => _isServer;
            set => _isServer = value;
        }

        private bool _directConnected = false;

        // Set while we are intentionally tearing down a client connection.
        // Callbacks arriving from the direct connect transport during teardown
        // must not be mistaken for a *failed* direct connection, which would
        // trigger the relay fallback in DirectDisconnected() and silently
        // rejoin the room we just left.
        private bool _intentionalDisconnect = false;

        // The relay can send RequestNATConnection more than once; this keeps us
        // from stacking multiple pending BeginReceive calls on the same socket.
        private bool _natReceiveStarted = false;

        private bool _isAuthenticated = false;
        private int _currentMemberId;
        private bool _callbacksInitialized = false;
        private string _cachedHostID;
        private UdpClient _NATPuncher;
        private IPEndPoint _NATIP;
        private IPEndPoint _relayPuncherIP;
        private byte[] _punchData = new byte[1] { 1 };
        private IPEndPoint _directConnectEndpoint;
        private SocketProxy _clientProxy;
        private BiDictionary<IPEndPoint, SocketProxy> _serverProxies = new BiDictionary<IPEndPoint, SocketProxy> ();
        private BiDictionary<int, int> _connectedRelayClients = new BiDictionary<int, int> ();
        private BiDictionary<int, int> _connectedDirectClients = new BiDictionary<int, int> ();
        private bool _serverListUpdated = false;
    }

    public enum LRMRegions { Any, NorthAmerica, SouthAmerica, Europe, Asia, Africa, Oceania }
}