using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using kcp2k;
using Mirror;
using Mirror.SimpleWeb;
using UnityEngine;

namespace LightReflectiveMirror {
    [DefaultExecutionOrder (1001)]
    public partial class LightReflectiveMirrorTransport : Transport {
        public bool IsAuthenticated () => _isAuthenticated;

        private void Awake () {
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                useNATPunch = false;
            else
                _directConnectModule = GetComponent<LRMDirectConnectModule> ();

            if (clientToServerTransport is LightReflectiveMirrorTransport)
                throw new Exception ("Haha real funny... Use a different transport.");

            if (_directConnectModule != null) {
                if (useNATPunch && !_directConnectModule.SupportsNATPunch ()) {
                    Debug.LogWarning ("[LRM] NAT punch is enabled but the direct connect transport does not support it. Disabling NAT punch. Use KcpTransport (or Ignorance) as the direct connect transport if you need it.");
                    useNATPunch = false;
                }
            } else if (useNATPunch) {
                // WebGL already cleared useNATPunch above, so reaching here means a
                // non-WebGL build that asked for NAT punch without the module.
                Debug.LogWarning ("[LRM] NAT punch is enabled but no LRMDirectConnectModule component was found on this GameObject. NAT punch and direct connect will be inactive.");
            }

            SetupCallbacks ();

            if (connectOnAwake)
                ConnectToRelay ();

            InvokeRepeating (nameof (SendHeartbeat), heartBeatInterval, heartBeatInterval);
        }

        private void SetupCallbacks () {
            if (_callbacksInitialized)
                return;

            _callbacksInitialized = true;
            clientToServerTransport.OnClientConnected = OnConnectedToRelay;
            clientToServerTransport.OnClientDataReceived = DataReceived;
            clientToServerTransport.OnClientDisconnected = Disconnected;
            clientToServerTransport.OnClientError = (transportError, reason) => Debug.LogError ($"[LRM] OnClientError: {transportError} Reason: {reason}");
        }

        private void Disconnected () {
            _connectedToRelay = false;
            _isAuthenticated = false;
            disconnectedFromRelay?.Invoke ();
            serverStatus = "Disconnected from relay.";
        }

        private void OnConnectedToRelay () {
            _connectedToRelay = true;
            connectedToRelay?.Invoke ();
        }

        public void ConnectToRelay () {
            if (!useLoadBalancer) {
                if (!_connectedToRelay) {
                    Connect (serverIP, serverPort);
                }
            } else {
                if (!_connectedToRelay) {
                    StartCoroutine (RelayConnect ());
                }
            }
        }

        /// <summary>
        /// Connects to the desired relay
        /// </summary>
        /// <param name="serverIP"></param>
        private void Connect (string serverIP, ushort port = 7777) {
            // need to implement custom port
            if (clientToServerTransport is LightReflectiveMirrorTransport)
                throw new Exception ("LRM | Client to Server Transport cannot be LRM.");

            SetTransportPort (port);

            this.serverIP = serverIP;
            serverStatus = "Connecting to relay...";
            _clientSendBuffer = new byte[clientToServerTransport.GetMaxPacketSize () * 2];
            clientToServerTransport.ClientConnect (serverIP);
        }

        public void DisconnectFromRelay () {
            if (IsAuthenticated ()) {
                clientToServerTransport.ClientDisconnect ();
            }
        }

        private void SendHeartbeat () {
            if (_connectedToRelay) {
                // Send a blank message with just the opcode for heartbeat
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.Heartbeat);

                try {
                    clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
                } catch (Exception e) {
                    Debug.LogError ($"[LRM] Failed to send heartbeat: {e.Message}");
                }

                // If NAT Puncher is initialized, send heartbeat on that as well.
                try {
                    if (_NATPuncher != null && _relayPuncherIP != null) {
                        _NATPuncher.Send (new byte[] { 0 }, 1, _relayPuncherIP);
                    }
                } catch (Exception e) {
                    Debug.LogError ($"[LRM] NAT heartbeat failed: {e.Message}");
                }

                // Check if any server-side proxies havent been used in 10 seconds, and timeout if so.
                var keys = new List<IPEndPoint> (_serverProxies.GetAllKeys ());

                for (int i = 0; i < keys.Count; i++) {
                    if (DateTime.Now.Subtract (_serverProxies.GetByFirst (keys[i]).lastInteractionTime).TotalSeconds > 10) {
                        _serverProxies.GetByFirst (keys[i]).Dispose ();
                        _serverProxies.Remove (keys[i]);
                    }
                }
            }
        }

        private void DataReceived (ArraySegment<byte> segmentData, int channel) {
            try {
                var data = segmentData.Array;
                int pos = segmentData.Offset;

                if (data == null) {
                    Debug.LogError ("[LRM] Data array is null!");
                    return;
                }

                if (pos >= data.Length) {
                    Debug.LogError ($"[LRM] Invalid offset position {pos} for data length {data.Length}");
                    return;
                }

                // Read the opcode of the incoming data, this allows us to know what its used for.
                OpCodes opcode = (OpCodes) data.ReadByte (ref pos);

                switch (opcode) {
                    case OpCodes.Authenticated:
                        serverStatus = "Authenticated! Good to go!";
                        _isAuthenticated = true;
                        RequestServerList ();
                        break;

                    case OpCodes.AuthenticationRequest:
                        serverStatus = "Sent authentication to relay...";
                        SendAuthKey ();
                        break;

                    case OpCodes.GetData:
                        var recvData = data.ReadBytes (ref pos);

                        if (IsServer) {
                            int senderId = data.ReadInt (ref pos);
                            if (_connectedRelayClients.TryGetByFirst (senderId, out int clientID)) {
                                OnServerDataReceived?.Invoke (clientID, new ArraySegment<byte> (recvData), channel);
                            }
                        }

                        if (IsClient) {
                            OnClientDataReceived?.Invoke (new ArraySegment<byte> (recvData), channel);
                        }

                        break;

                    case OpCodes.ServerLeft:
                        if (IsClient) {
                            IsClient = false;
                            OnClientDisconnected?.Invoke ();
                        }

                        break;

                    case OpCodes.PlayerDisconnected:
                        if (IsServer) {
                            int user = data.ReadInt (ref pos);
                            if (_connectedRelayClients.TryGetByFirst (user, out int clientID)) {
                                OnServerDisconnected?.Invoke (clientID);
                                _connectedRelayClients.Remove (user);
                            }
                        }

                        break;

                    case OpCodes.RoomCreated:
                        serverId = data.ReadString (ref pos);
                        break;

                    case OpCodes.ServerJoined:
                        int clientId = data.ReadInt (ref pos);

                        if (IsClient) {
                            OnClientConnected?.Invoke ();
                        }

                        if (IsServer) {
                            _connectedRelayClients.Add (clientId, _currentMemberId);
                            OnServerConnected?.Invoke (_currentMemberId);
                            _currentMemberId++;
                        }

                        break;

                    case OpCodes.DirectConnectIP:
                        var ip = data.ReadString (ref pos);
                        int port = data.ReadInt (ref pos);
                        bool attemptNatPunch = data.ReadBool (ref pos);

                        _directConnectEndpoint = new IPEndPoint (IPAddress.Parse (ip), port);

                        if (useNATPunch && attemptNatPunch) {
                            StartCoroutine (NATPunch (_directConnectEndpoint));
                        }

                        if (!IsServer) {
                            if (_clientProxy == null && useNATPunch && attemptNatPunch) {
                                _clientProxy = new SocketProxy (_NATIP.Port - 1);
                                _clientProxy.dataReceived += ClientProcessProxyData;
                            }

                            if (useNATPunch && attemptNatPunch) {
                                if (ip == LOCALHOST) {
                                    _directConnectModule.JoinServer (LOCALHOST, port + 1);
                                } else {
                                    _directConnectModule.JoinServer (LOCALHOST, _NATIP.Port - 1);
                                }
                            } else {
                                _directConnectModule.JoinServer (ip, port);
                            }
                        }

                        break;

                    case OpCodes.RequestNATConnection:
                        // Called when the LRM node would like us to establish a NAT puncher connection. Its safe to ignore if NAT punch is disabled.
                        if (useNATPunch && GetLocalIp () != null && _directConnectModule != null) {
                            try {
                                // Read data in the exact order the server sends it
                                string natID = data.ReadString (ref pos);
                                NATPunchtroughPort = data.ReadInt (ref pos);

                                // Create NAT puncher if needed
                                if (_NATPuncher == null) {
                                    SetupNATPuncher ();
                                }

                                // Resolve server address
                                IPAddress serverAddr;
                                if (!IPAddress.TryParse (serverIP, out serverAddr)) {
                                    serverAddr = Dns.GetHostEntry (serverIP).AddressList[0];
                                }

                                _relayPuncherIP = new IPEndPoint (serverAddr, NATPunchtroughPort);

                                // Prepare response data (matches server expectation)
                                byte[] initialData = new byte[150];
                                int sendPos = 0;
                                initialData.WriteBool (ref sendPos, true); // Connection established flag
                                initialData.WriteString (ref sendPos, natID); // Echo back the natID

                                // Send NAT punch attempts (original approach)
                                for (int attempts = 0; attempts < NAT_PUNCH_ATTEMPTS; attempts++) {
                                    _NATPuncher.Send (initialData, sendPos, _relayPuncherIP);
                                }

                                // Only one receive pending at a time. If a previous
                                // chain died on a socket error this re-arms it.
                                if (!_natReceiveStarted)
                                    BeginNATReceive ();
                            } catch (Exception ex) {
                                Debug.LogError ($"[LRM] NAT setup failed - {ex.Message}");
                            }
                        } else if (useNATPunch) {
                            // Relay asked us to punch but we cannot. Say why - this is
                            // otherwise a silent failure that looks like "NAT punch is broken".
                            if (_directConnectModule == null)
                                Debug.LogWarning ("[LRM] Relay requested a NAT connection but no LRMDirectConnectModule is present. Ignoring.");
                            else if (GetLocalIp () == null)
                                Debug.LogWarning ("[LRM] Relay requested a NAT connection but no local IPv4 address could be resolved. Ignoring.");
                        }

                        break;

                    case OpCodes.Heartbeat:
                        // Heartbeat received, do nothing
                        break;

                    default:
                        break;
                }
            } catch (Exception e) {
                Debug.LogError ($"[LRM] Error in DataReceived: {e.Message}\nSegment: offset={segmentData.Offset}, count={segmentData.Count}");
            }
        }

        public void SetTransportPort (ushort port) {
            if (clientToServerTransport is KcpTransport kcp)
                kcp.Port = port;

            if (clientToServerTransport is TelepathyTransport telepathy)
                telepathy.port = port;

            if (clientToServerTransport is SimpleWebTransport swt)
                swt.port = port;
        }

        public void UpdateRoomName (string newServerName = "My Awesome Server!") {
            if (IsServer) {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.UpdateRoomData);

                _clientSendBuffer.WriteBool (ref pos, true);
                _clientSendBuffer.WriteString (ref pos, newServerName);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            }
        }

        public void UpdateRoomData (string newServerData = "Extra Data!") {
            if (IsServer) {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.UpdateRoomData);

                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, true);
                _clientSendBuffer.WriteString (ref pos, newServerData);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            }
        }

        public void UpdateRoomVisibility (bool isPublic = true) {
            if (IsServer) {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.UpdateRoomData);

                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, true);
                _clientSendBuffer.WriteBool (ref pos, isPublic);
                _clientSendBuffer.WriteBool (ref pos, false);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            }
        }

        public void UpdateRoomPlayerCount (int maxPlayers = 16) {
            if (IsServer) {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.UpdateRoomData);

                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteBool (ref pos, true);
                _clientSendBuffer.WriteInt (ref pos, maxPlayers);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            }
        }

        private Room? GetServerForID (string serverID) {
            for (int i = 0; i < relayServerList.Count; i++) {
                if (relayServerList[i].serverId == serverID)
                    return relayServerList[i];
            }

            return null;
        }

        private void SendAuthKey () {
            int pos = 0;
            _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.AuthenticationResponse);
            _clientSendBuffer.WriteString (ref pos, authenticationKey);

            clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
        }

        public enum OpCodes {
            Default = 0,
            RequestID = 1,
            JoinServer = 2,
            SendData = 3,
            GetID = 4,
            ServerJoined = 5,
            GetData = 6,
            CreateRoom = 7,
            ServerLeft = 8,
            PlayerDisconnected = 9,
            RoomCreated = 10,
            LeaveRoom = 11,
            KickPlayer = 12,
            AuthenticationRequest = 13,
            AuthenticationResponse = 14,
            Authenticated = 17,
            UpdateRoomData = 18,
            ServerConnectionData = 19,
            RequestNATConnection = 20,
            DirectConnectIP = 21,
            Heartbeat = 200
        }

        private static string GetLocalIp () {
            var host = Dns.GetHostEntry (Dns.GetHostName ());
            foreach (var ip in host.AddressList) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) {
                    return ip.ToString ();
                }
            }

            return null;
        }

        /// <summary>
        /// Releases every socket the NAT/direct-connect path owns and clears the
        /// state derived from them, so a later session starts clean instead of
        /// reusing a dead receive chain or a stale port.
        /// </summary>
        private void TeardownNATPuncher () {
            _natReceiveStarted = false;

            if (_NATPuncher != null) {
                try {
                    _NATPuncher.Dispose ();
                } catch (Exception e) {
                    Debug.LogWarning ($"[LRM] NAT puncher dispose failed: {e.Message}");
                }

                _NATPuncher = null;
            }

            _NATIP = null;
            _relayPuncherIP = null;

            if (_clientProxy != null) {
                _clientProxy.Dispose ();
                _clientProxy = null;
            }

            var proxyKeys = new List<IPEndPoint> (_serverProxies.GetAllKeys ());

            for (int i = 0; i < proxyKeys.Count; i++) {
                _serverProxies.GetByFirst (proxyKeys[i]).Dispose ();
                _serverProxies.Remove (proxyKeys[i]);
            }
        }

        private void SetupNATPuncher () {
            try {
                string localIp = GetLocalIp ();

                if (localIp == null)
                    throw new InvalidOperationException ("no local IPv4 address could be resolved");

                _NATPuncher = new UdpClient { ExclusiveAddressUse = false };

                // Pick a free port in 16000-17000. Bounded: an unbounded retry here
                // hangs the Unity main thread outright if binding never succeeds.
                Exception lastBindError = null;
                bool bound = false;

                for (int attempt = 0; attempt < NAT_PORT_BIND_ATTEMPTS; attempt++) {
                    try {
                        _NATIP = new IPEndPoint (IPAddress.Parse (localIp), UnityEngine.Random.Range (16000, 17000));
                        _NATPuncher.Client.SetSocketOption (SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        _NATPuncher.Client.Bind (_NATIP);
                        bound = true;
                        break;
                    } catch (Exception bindError) {
                        // Port in use, keep trying.
                        lastBindError = bindError;
                    }
                }

                if (!bound) {
                    _NATIP = null;
                    throw new InvalidOperationException (
                        $"could not bind a NAT puncher port in {NAT_PORT_BIND_ATTEMPTS} attempts - last error: {lastBindError?.Message}");
                }
            } catch (Exception ex) {
                Debug.LogError ($"[LRM] Failed to create NAT puncher: {ex.Message}");
                _NATPuncher?.Dispose ();
                _NATPuncher = null;
                throw;
            }
        }

    }

    [Serializable]
    public struct Room {
        public string serverName;
        public int maxPlayers;
        public string serverId;
        public string serverData;
        public int hostId;
        public int appId;
        public string version;
        public List<int> clients;
        public int currentPlayers;
        public RelayAddress relayInfo;
    }

    [Serializable]
    public struct RelayAddress {
        public ushort port;
        public ushort endpointPort;
        public string address;
        public LRMRegions serverRegion;
    }
}