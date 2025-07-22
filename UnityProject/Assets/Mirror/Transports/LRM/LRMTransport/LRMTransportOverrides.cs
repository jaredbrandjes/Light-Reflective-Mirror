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

            if (_connectedDirectClients.TryGetBySecond (connectionId, out int directId))
                return "DIRECT-" + directId;

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
                clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
            } else {
                StartCoroutine (JoinOtherRelayAndMatch (room, address));
            }
        }

        public override void ClientDisconnect () {
            if (IsClient) {
                _isClient = false;
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.LeaveRoom);

                // Send leave room notification
                if (Available ()) {
                    clientToServerTransport.ClientSend (new ArraySegment<byte> (_clientSendBuffer, 0, pos), 0);
                }
            }

            // if (_directConnectModule != null) _directConnectModule.ClientDisconnect(); //Commented due to breaking reconnection ability

            // Clean up state
            _directConnected = false;
            _cachedHostID = null;
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
                int pos = 0;
                _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.SendData);
                _clientSendBuffer.WriteBytes (ref pos, segment.Array.Take (segment.Count).ToArray ());
                _clientSendBuffer.WriteInt (ref pos, _connectedRelayClients.GetBySecond (connectionId));

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

            var keys = new List<IPEndPoint> (_serverProxies.GetAllKeys ());

            for (int i = 0; i < keys.Count; i++) {
                _serverProxies.GetByFirst (keys[i]).Dispose ();
                _serverProxies.Remove (keys[i]);
            }

            int pos = 0;
            _clientSendBuffer.WriteByte (ref pos, (byte) OpCodes.CreateRoom);

            _clientSendBuffer.WriteInt (ref pos, maxServerPlayers);
            _clientSendBuffer.WriteString (ref pos, serverName);
            _clientSendBuffer.WriteBool (ref pos, isPublicServer);
            _clientSendBuffer.WriteString (ref pos, extraServerData);

            // If we have direct connect module, and our local IP isnt null, tell server
            _clientSendBuffer.WriteBool (ref pos, _directConnectModule != null ? GetLocalIp () != null : false);

            if (_directConnectModule != null && GetLocalIp () != null && useNATPunch) {
                _clientSendBuffer.WriteString (ref pos, GetLocalIp ());
                _directConnectModule.StartServer (useNATPunch ? _NATIP.Port + 1 : -1);
            } else
                _clientSendBuffer.WriteString (ref pos, "0.0.0.0");

            if (useNATPunch) {
                _clientSendBuffer.WriteBool (ref pos, true);
                _clientSendBuffer.WriteInt (ref pos, 0);
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

                var keys = new List<IPEndPoint> (_serverProxies.GetAllKeys ());

                for (int i = 0; i < keys.Count; i++) {
                    _serverProxies.GetByFirst (keys[i]).Dispose ();
                    _serverProxies.Remove (keys[i]);
                }
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
            clientToServerTransport.Shutdown ();
        }
    }
}