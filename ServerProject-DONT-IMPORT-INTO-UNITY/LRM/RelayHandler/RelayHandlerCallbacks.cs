using System;
using LightReflectiveMirror.Endpoints;

namespace LightReflectiveMirror {
    public partial class RelayHandler {
        /// <summary>
        /// Invoked when a client connects to this LRM server.
        /// </summary>
        /// <param name="clientId">The ID of the client who connected.</param>
        public void ClientConnected (int clientId) {
            _pendingAuthentication.Add (clientId);
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

                OpCodes opcode = (OpCodes) data.ReadByte (ref pos);

                if (_pendingAuthentication.Contains (clientId)) {
                    if (opcode == OpCodes.AuthenticationResponse) {
                        string authResponse = data.ReadString (ref pos);
                        if (authResponse == Program.conf.AuthenticationKey) {
                            _pendingAuthentication.Remove (clientId);
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
                            int _maxPlayers = data.ReadInt (ref pos);
                            string _serverName = data.ReadString (ref pos);
                            bool _isPublic = data.ReadBool (ref pos);
                            string _serverData = data.ReadString (ref pos);
                            bool _useDirectConnect = data.ReadBool (ref pos);
                            string _hostLocalIP = data.ReadString (ref pos);
                            bool _useNatPunch = data.ReadBool (ref pos);
                            int _port = data.ReadInt (ref pos);
                            int _appId = data.ReadInt (ref pos);
                            string _version = data.ReadString (ref pos);

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
                        string _serverId = data.ReadString (ref pos);
                        bool canDirectConnect = data.ReadBool (ref pos);
                        string localIP = data.ReadString (ref pos);

                        JoinRoom (clientId, _serverId, canDirectConnect, localIP);
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}] [{_serverId}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                        break;
                    case OpCodes.KickPlayer:
                        LeaveRoom (data.ReadInt (ref pos), clientId);
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}]");
                        break;
                    case OpCodes.SendData:
                        ProcessData (clientId, data.ReadBytes (ref pos), channel, data.ReadInt (ref pos));
                        break;
                    case OpCodes.UpdateRoomData:
                        Program.WriteLogMessage ($"Client [{clientId}] [{opcode}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                        var plyRoom = _cachedClientRooms[clientId];

                        if (plyRoom == null || plyRoom.hostId != clientId)
                            return;

                        if (data.ReadBool (ref pos))
                            plyRoom.serverName = data.ReadString (ref pos);

                        if (data.ReadBool (ref pos))
                            plyRoom.serverData = data.ReadString (ref pos);

                        if (data.ReadBool (ref pos))
                            plyRoom.isPublic = data.ReadBool (ref pos);

                        if (data.ReadBool (ref pos))
                            plyRoom.maxPlayers = data.ReadInt (ref pos);

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
        public void HandleDisconnect (int clientId) => LeaveRoom (clientId);
    }
}