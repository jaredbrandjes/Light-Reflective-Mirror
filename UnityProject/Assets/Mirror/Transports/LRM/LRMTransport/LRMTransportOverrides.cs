using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Mirror;
using UnityEngine;

namespace LightReflectiveMirror {
    public partial class LightReflectiveMirrorTransport : Transport {
        public override bool ServerActive () => IsServer;
        public override bool Available () => _connectedToRelay;
        public override void ClientConnect (Uri uri) => ClientConnect (uri.Host);
        public override int GetMaxPacketSize (int channelId = 0) => clientToServerTransport.GetMaxPacketSize (channelId);
        public override bool ClientConnected () => IsClient;

        public override void ServerLateUpdate () {
            if (_directConnectModule != null)
                _directConnectModule.directConnectTransport.ServerLateUpdate ();
        }

        public override string ServerGetClientAddress (int connectionId) {
            if (_connectedRelayClients.TryGetBySecond (connectionId, out int relayId))
                return relayId.ToString ();

            if (_connectedDirectClients.TryGetBySecond (connectionId, out int directId)) {
                if (_directClientAddresses.TryGetValue (connectionId, out string address))
                    return address;

                return "DIRECT-" + directId;
            }

            // Shouldn't ever get here.
            return "?";
        }

        public override void ClientEarlyUpdate () {
            clientToServerTransport.ClientEarlyUpdate ();

            if (_directConnectModule != null)
                _directConnectModule.directConnectTransport.ClientEarlyUpdate ();
        }

        public override void ClientLateUpdate () {
            clientToServerTransport.ClientLateUpdate ();

            if (_directConnectModule != null)
                _directConnectModule.directConnectTransport.ClientLateUpdate ();
        }

        public override void ServerEarlyUpdate () {
            if (_directConnectModule != null)
                _directConnectModule.directConnectTransport.ServerEarlyUpdate ();
        }

        public override void ClientConnect (string address) {
            if (!Available ()) {
                Debug.Log ("Not connected to relay!");
                OnClientDisconnected?.Invoke ();
                return;
            }

            if (IsClient || IsServer)
                throw new Exception ("Cannot connect while hosting/already connected!");

            _cachedHostID = address;

            var room = GetServerForID (address);

            if (!useLoadBalancer) {
                int pos = 0;
                _directConnected = false;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.JoinServer);
                _clientSendBuffer.WriteString (ref pos, address);
                _clientSendBuffer.WriteBool (ref pos, _directConnectModule != null);
                _clientSendBuffer.WriteString (ref pos, GetLocalIp () ?? "0.0.0.0");

                IsClient = true;
                _joinedRelayRoom = true;
                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            } else {
                StartCoroutine (JoinOtherRelayAndMatch (room, address));
            }
        }

        public override void ClientDisconnect () {
            // Mirror re-enters this via the OnClientDisconnected invoke below;
            // bail out on the second pass instead of sending LeaveRoom twice.
            if (_intentionalDisconnect)
                return;

            _intentionalDisconnect = true;

            try {
                bool wasClient = IsClient;
                bool wasDirect = _directConnected;
                bool wasInRoom = _joinedRelayRoom;

                _isClient = false;
                _directConnected = false;
                _joinedRelayRoom = false;

                // Keyed off _joinedRelayRoom, not _isClient. A failed direct
                // connection clears _isClient in DirectDisconnected before Mirror
                // reaches here, and skipping LeaveRoom then left the relay holding
                // our slot until the whole relay session dropped - two of those
                // permanently fill a 2-player room.
                if (wasInRoom && Available ()) {
                    int pos = 0;
                    _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.LeaveRoom);
                    clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
                }

                // Only tear down the direct transport if we actually had a direct
                // connection. _intentionalDisconnect stops the callback this
                // raises from falling back to the relay and rejoining the room.
                if (wasDirect && _directConnectModule != null)
                    _directConnectModule.ClientDisconnect ();

                if (_clientProxy != null) {
                    _clientProxy.Dispose ();
                    _clientProxy = null;
                }

                _cachedHostID = null;

                // Mirror blocks its own client shutdown until the transport
                // reports the disconnect. Without this, NetworkClient stays in
                // ConnectState.Disconnecting and StopClient() never returns the
                // player to the offline scene.
                if (wasClient)
                    OnClientDisconnected?.Invoke ();
            } finally {
                _intentionalDisconnect = false;
            }
        }

        public override void ClientSend (ArraySegment<byte> segment, int channelId) {
            if (_directConnected) {
                _directConnectModule.ClientSend (segment, channelId);
            } else {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.SendData);
                _clientSendBuffer.WriteBytes (ref pos, segment.Array.Take (segment.Count).ToArray ());
                _clientSendBuffer.WriteInt (ref pos, 0);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), channelId);
            }
        }

        public override void ServerDisconnect (int connectionId) {
            if (_connectedRelayClients.TryGetBySecond (connectionId, out int relayId)) {
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.KickPlayer);
                _clientSendBuffer.WriteInt (ref pos, relayId);
                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
                return;
            }

            if (_connectedDirectClients.TryGetBySecond (connectionId, out int directId))
                _directConnectModule.KickClient (directId);
        }

        public override void ServerSend (int connectionId, ArraySegment<byte> segment, int channelId) {
            if (_directConnectModule != null && _connectedDirectClients.TryGetBySecond (connectionId, out int directId)) {
                _directConnectModule.ServerSend (directId, segment, channelId);
            } else {
                // Mirror can call ServerSend for a connection we have already
                // dropped; an unguarded GetBySecond throws KeyNotFoundException
                // out of the send loop and takes the whole server down.
                if (!_connectedRelayClients.TryGetBySecond (connectionId, out int relayId)) {
                    Debug.LogWarning ($"[LRM] Dropped a send to unknown connection {connectionId}.");
                    return;
                }

                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.SendData);
                _clientSendBuffer.WriteBytes (ref pos, segment.Array.Take (segment.Count).ToArray ());
                _clientSendBuffer.WriteInt (ref pos, relayId);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), channelId);
            }
        }

        public override void ServerStart () {
            if (!Available ()) {
                Debug.Log ("Not connected to relay! Server failed to start.");
                return;
            }

            if (IsClient || IsServer) {
                Debug.Log ("Cannot host while already hosting or connected!");
                return;
            }

            IsServer = true;
            _connectedRelayClients = new BiDictionary<int, int> ();
            _currentMemberId = 1;
            _connectedDirectClients = new BiDictionary<int, int> ();

            ClearServerProxies ();

            int pos = 0;
            _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.CreateRoom);

            // The relay counts the room creator as an occupant - Room.currentPlayers
            // is clients.Count + 1 - which is right for a host but wrong for a
            // dedicated server, which is not a player. Advertise one extra slot in
            // that case so maxServerPlayers always means "players who can play",
            // in both modes and without changing the CreateRoom wire format.
            // Mirror sets mode before Listen(), so it is already correct here.
            bool creatorIsPlayer = NetworkManager.singleton == null
                || NetworkManager.singleton.mode != NetworkManagerMode.ServerOnly;

            _clientSendBuffer.WriteInt (ref pos, creatorIsPlayer ? maxServerPlayers : maxServerPlayers + 1);
            _clientSendBuffer.WriteString (ref pos, serverName);
            _clientSendBuffer.WriteBool (ref pos, isPublicServer);
            _clientSendBuffer.WriteString (ref pos, extraServerData);

            // Resolve once - GetLocalIp() does a DNS lookup on every call.
            string localIp = GetLocalIp ();
            bool canDirectConnect = _directConnectModule != null && localIp != null;

            // NAT punch needs the puncher socket, which only exists once the relay
            // has sent RequestNATConnection. Decide per-host attempt rather than
            // mutating the serialized useNATPunch field, so a later host attempt
            // can still punch once that message has arrived.
            bool natPunchActive = canDirectConnect && useNATPunch && _NATIP != null;

            if (canDirectConnect && useNATPunch && _NATIP == null) {
                Debug.LogWarning ("[LRM] Hosting without NAT punch: the relay has not requested a NAT connection yet. If this persists, check EnableNATPunchtroughServer on the node.");
            }

            // If we have direct connect module, and our local IP isnt null, tell server
            _clientSendBuffer.WriteBool (ref pos, canDirectConnect);

            if (natPunchActive) {
                _clientSendBuffer.WriteString (ref pos, localIp);
                _directConnectModule.StartServer (_NATIP.Port + 1);
            } else {
                _clientSendBuffer.WriteString (ref pos, "0.0.0.0");
            }

            if (natPunchActive) {
                _clientSendBuffer.WriteBool (ref pos, true);

                // Report the local puncher port rather than the 0 this used to
                // send. In NAT punch mode the relay previously ignored this field
                // entirely, so older nodes are unaffected; newer ones use it for
                // peers on this same machine, where the punched external port is
                // meaningless. The client derives our direct server from port + 1,
                // which is exactly where StartServer bound it above.
                _clientSendBuffer.WriteInt (ref pos, _NATIP.Port);
            } else {
                _clientSendBuffer.WriteBool (ref pos, false);
                _clientSendBuffer.WriteInt (ref pos, _directConnectModule == null ? 1 : _directConnectModule.SupportsNATPunch () ? _directConnectModule.GetTransportPort () : 1);
            }

            _clientSendBuffer.WriteInt (ref pos, appId);
            _clientSendBuffer.WriteString (ref pos, Application.version);

            clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
        }

        public override void ServerStop () {
            if (IsServer) {
                IsServer = false;
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.LeaveRoom);

                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);

                if (_directConnectModule != null)
                    _directConnectModule.StopServer ();

                ClearServerProxies ();
            }
        }

        public override Uri ServerUri () {
            UriBuilder builder = new UriBuilder {
                Scheme = "LRM",
                Host = serverId.ToString ()
            };

            return builder.Uri;
        }

        public override void Shutdown () {
            DisconnectFromRelay ();
            _isAuthenticated = false;
            IsClient = false;
            IsServer = false;
            _connectedToRelay = false;

            // Release the NAT puncher and proxies. Without this the UDP socket
            // stays bound and _natReceiveStarted keeps the next session from
            // arming a receive on the replacement socket.
            TeardownNATPuncher ();

            clientToServerTransport.Shutdown ();
        }
    }
}