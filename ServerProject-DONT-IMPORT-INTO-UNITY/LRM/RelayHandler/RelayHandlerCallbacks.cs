using System;
using System.Linq;
using LightReflectiveMirror.Endpoints;

namespace LightReflectiveMirror {
    public partial class RelayHandler {
        /// <summary>
        /// Invoked when a client connects to this LRM server.
        /// </summary>
        /// <param name="clientId">The ID of the client who connected.</param>
        public void ClientConnected (int clientId) {
            _pendingAuthentication[clientId] = DateTime.UtcNow;
            var buffer = _sendBuffers.Rent (1);
            int pos = 0;
            buffer.WriteByte (ref pos, (byte) OpCodes.AuthenticationRequest);
            Program.transport.ServerSend (clientId, new ArraySegment<byte> (buffer, 0, pos), 0);
            _sendBuffers.Return (buffer);
        }

        /// <summary>
        /// Handles the processing of data from a client.
        /// </summary>
        /// <param name="clientId">The client who sent the data</param>
        /// <param name="segmentData">The binary data</param>
        /// <param name="channel">The channel the client sent the data on</param>
        public void HandleMessage (int clientId, ArraySegment<byte> segmentData, int channel) {
            try {
                var data = segmentData.Array;
                int pos = segmentData.Offset;
                int end = segmentData.Offset + segmentData.Count;

                OpCodes opcode = (OpCodes) data.ReadByte (ref pos, end);

                if (!_authenticatedClients.Contains (clientId)) {
                    if (_pendingAuthentication.ContainsKey (clientId) && opcode == OpCodes.AuthenticationResponse) {
                        string authResponse = data.ReadString (ref pos, end);
                        _pendingAuthentication.Remove (clientId);

                        if (authResponse == Program.conf.AuthenticationKey) {
                            _authenticatedClients.Add (clientId);
                            int writePos = 0;
                            var sendBuffer = _sendBuffers.Rent (1);
                            sendBuffer.WriteByte (ref writePos, (byte) OpCodes.Authenticated);
                            Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendBuffer, 0, writePos), 0);

                            _sendBuffers.Return (sendBuffer);
                        } else {
                            Program.WriteLogMessage ($"Client {clientId} sent wrong auth key! Removing from LRM node.");
                            Program.transport.ServerDisconnect (clientId);
                        }
                    }

                    return;
                }

                switch (opcode) {
                    case OpCodes.CreateRoom:
                        try {
                            int _maxPlayers = data.ReadInt (ref pos, end);
                            string _serverName = data.ReadString (ref pos, end);
                            bool _isPublic = data.ReadBool (ref pos, end);
                            string _serverData = data.ReadString (ref pos, end);
                            bool _useDirectConnect = data.ReadBool (ref pos, end);
                            string _hostLocalIP = data.ReadString (ref pos, end);
                            bool _useNatPunch = data.ReadBool (ref pos, end);
                            int _port = data.ReadInt (ref pos, end);
                            int _appId = data.ReadInt (ref pos, end);
                            string _version = data.ReadString (ref pos, end);

                            CreateRoom (clientId, _maxPlayers, _serverName, _isPublic, _serverData,
                                _useDirectConnect, _hostLocalIP, _useNatPunch, _port, _appId, _version);
                        } catch (Exception e) {
                            Program.WriteLogMessage ($"CreateRoom failed at position {pos}: {e.Message}");
                            throw;
                        }
                        break;
                    case OpCodes.RequestID:
                        SendClientID (clientId);
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}]");
                        break;
                    case OpCodes.LeaveRoom:
                        LeaveRoom (clientId);
                        break;
                    case OpCodes.JoinServer:
                        string _serverId = data.ReadString (ref pos, end);
                        bool canDirectConnect = data.ReadBool (ref pos, end);
                        string localIP = data.ReadString (ref pos, end);

                        JoinRoom (clientId, _serverId, canDirectConnect, localIP);
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}] [{_serverId}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                        break;
                    case OpCodes.KickPlayer:
                        LeaveRoom (data.ReadInt (ref pos, end), clientId);
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}]");
                        break;
                    case OpCodes.SendData:
                        ProcessData (clientId, data.ReadBytes (ref pos, end), channel, data.ReadInt (ref pos, end));
                        break;
                    case OpCodes.UpdateRoomData:
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                        var plyRoom = _cachedClientRooms[clientId];

                        if (plyRoom == null || plyRoom.hostId != clientId)
                            return;

                        if (data.ReadBool (ref pos, end))
                            plyRoom.serverName = Truncate (data.ReadString (ref pos, end), Program.conf.MaxServerNameLength);

                        if (data.ReadBool (ref pos, end))
                            plyRoom.serverData = Truncate (data.ReadString (ref pos, end), Program.conf.MaxServerDataLength);

                        if (data.ReadBool (ref pos, end))
                            plyRoom.isPublic = data.ReadBool (ref pos, end);

                        if (data.ReadBool (ref pos, end))
                            plyRoom.maxPlayers = data.ReadInt (ref pos, end);

                        Endpoint.RoomsModified ();
                        break;
                }
            } catch {
                // sent invalid data, boot them hehe
                Program.WriteLogMessage ($"Client {clientId} sent bad data! Removing from LRM node.");
                Program.transport.ServerDisconnect (clientId);
            }
        }

        /// <summary>
        /// Invoked when a client disconnects from the relay.
        /// </summary>
        /// <param name="clientId">The ID of the client who disconnected</param>
        public void HandleDisconnect (int clientId) {
            _pendingAuthentication.Remove (clientId);
            _authenticatedClients.Remove (clientId);
            LeaveRoom (clientId);
        }

        /// <summary>
        /// Disconnects clients that connected but never sent the auth key
        /// within AuthenticationTimeout, so idle connections cannot pile up.
        /// </summary>
        public void DisconnectUnauthenticatedClients () {
            if (Program.conf.AuthenticationTimeout <= 0)
                return;

            DateTime cutoff = DateTime.UtcNow.AddMilliseconds (-Program.conf.AuthenticationTimeout);

            // copied: ServerDisconnect can call HandleDisconnect, which edits the dictionary
            foreach (int clientId in _pendingAuthentication.Where (x => x.Value < cutoff).Select (x => x.Key).ToList ()) {
                Program.WriteLogMessage ($"Client {clientId} did not authenticate in time! Removing from LRM node.");
                Program.transport.ServerDisconnect (clientId);
            }
        }
    }
}