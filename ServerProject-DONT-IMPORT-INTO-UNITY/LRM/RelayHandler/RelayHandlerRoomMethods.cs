using System;
using System.Collections.Generic;
using System.Net;
using LightReflectiveMirror.Endpoints;

namespace LightReflectiveMirror {
    public partial class RelayHandler {
        /// <summary>
        /// Creates a room on the LRM node.
        /// </summary>
        /// <param name="clientId">The client requesting to create a room</param>
        /// <param name="maxPlayers">The maximum amount of players for this room</param>
        /// <param name="serverName">The name for the server</param>
        /// <param name="isPublic">Whether or not the server should show up on the server list</param>
        /// <param name="serverData">Extra data the host can include</param>
        /// <param name="useDirectConnect">Whether or not, the host is capable of doing direct connections</param>
        /// <param name="hostLocalIP">The hosts local IP</param>
        /// <param name="useNatPunch">Whether or not, the host is supporting NAT Punch</param>
        /// <param name="port">The port of the direct connect transport on the host</param>
        private void CreateRoom (
            int clientId,
            int maxPlayers,
            string serverName,
            bool isPublic,
            string serverData,
            bool useDirectConnect,
            string hostLocalIP,
            bool useNatPunch,
            int port,
            int appId,
            string version,
            string providedServerId = null) {
            LeaveRoom (clientId);
            Program.instance.NATConnections.TryGetValue (clientId, out IPEndPoint hostIP);

            Room room = new () {
                hostId = clientId,
                maxPlayers = maxPlayers,
                serverName = serverName,
                isPublic = isPublic,
                serverData = serverData,
                appId = appId,
                version = version,
                clients = new List<int> (),
                serverId = providedServerId ?? GetRandomServerID (),
                hostIP = hostIP,
                hostLocalIP = hostLocalIP,
                supportsDirectConnect = hostIP != null && useDirectConnect,
                port = port,
                useNATPunch = useNatPunch,
                relayInfo = new RelayAddress {
                    address = Program.publicIP,
                    port = Program.conf.TransportPort,
                    endpointPort = Program.conf.EndpointPort,
                    serverRegion = Program.conf.LoadBalancerRegion
                }
            };

            rooms.Add (room);
            _cachedClientRooms.Add (clientId, room);
            _cachedRooms.Add (room.serverId, room);

            Console.WriteLine ($"[{DateTime.UtcNow}] Client [{clientId}] | Room Created [{room.serverId}]");

            // Use the common response method
            SendRoomCreatedResponse (clientId, room.serverId);

            Endpoint.RoomsModified ();
        }

        private void HandleRecreateRoom (int clientId, string serverId, int maxPlayers, string serverName, bool isPublic, string serverData, bool useDirectConnect, string hostLocalIP, bool useNatPunch, int port, int appId, string version) {
            Console.WriteLine ($"[{DateTime.UtcNow}] LRM Server | Attempting to recreate room with ID: {serverId}");

            if (_cachedRooms.TryGetValue (serverId, out Room existingRoom)) {
                // Update existing room
                existingRoom.hostId = clientId;
                existingRoom.maxPlayers = maxPlayers;
                existingRoom.serverName = serverName;
                existingRoom.isPublic = isPublic;
                existingRoom.serverData = serverData;
                existingRoom.appId = appId;
                existingRoom.version = version;
                existingRoom.clients.Clear ();
                existingRoom.hostLocalIP = hostLocalIP;
                existingRoom.supportsDirectConnect = useDirectConnect;
                existingRoom.port = port;
                existingRoom.useNATPunch = useNatPunch;

                _cachedClientRooms[clientId] = existingRoom;

                SendRoomCreatedResponse (clientId, serverId);
                Console.WriteLine ($"[{DateTime.UtcNow}] LRM Server | Room recreated successfully with ID: {serverId}");
            } else {
                // Attempt to create a new room with the provided server ID
                try {
                    CreateRoom (clientId, maxPlayers, serverName, isPublic, serverData, useDirectConnect, hostLocalIP, useNatPunch, port, appId, version, serverId);
                    Console.WriteLine ($"[{DateTime.UtcNow}] LRM Server | New room created with provided ID: {serverId}");
                } catch (Exception e) {
                    Console.WriteLine ($"[{DateTime.UtcNow}] LRM Server | Failed to create room with ID {serverId}: {e.Message}");
                    SendRecreateRoomFailedResponse (clientId, $"Failed to create room: {e.Message}");
                    return;
                }
            }

            Endpoint.RoomsModified ();
        }

        private void SendRoomCreatedResponse (int clientId, string serverId) {
            int pos = 0;
            byte[] sendBuffer = _sendBuffers.Rent (5);

            sendBuffer.WriteByte (ref pos, (byte) OpCodes.RoomCreated);
            sendBuffer.WriteString (ref pos, serverId);

            Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendBuffer, 0, pos), 0);
            _sendBuffers.Return (sendBuffer);
        }

        private void SendRecreateRoomFailedResponse (int clientId, string reason) {
            int pos = 0;
            byte[] sendBuffer = _sendBuffers.Rent (5);

            sendBuffer.WriteByte (ref pos, (byte) OpCodes.RecreateRoomFailed);
            sendBuffer.WriteString (ref pos, reason);

            Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendBuffer, 0, pos), 0);
            _sendBuffers.Return (sendBuffer);
        }

        /// <summary>
        /// Attempts to join a room for a client.
        /// </summary>
        /// <param name="clientId">The client requesting to join the room</param>
        /// <param name="serverId">The server ID of the room</param>
        /// <param name="canDirectConnect">If the client is capable of a direct connection</param>
        /// <param name="localIP">The local IP of the client joining</param>
        private void JoinRoom (int clientId, string serverId, bool canDirectConnect, string localIP) {
            LeaveRoom (clientId);

            if (_cachedRooms.ContainsKey (serverId)) {
                var room = _cachedRooms[serverId];

                if (room.clients.Count < room.maxPlayers) {
                    room.clients.Add (clientId);
                    _cachedClientRooms.Add (clientId, room);

                    int sendJoinPos = 0;
                    byte[] sendJoinBuffer = _sendBuffers.Rent (500);

                    if (canDirectConnect && Program.instance.NATConnections.ContainsKey (clientId) && room.supportsDirectConnect) {
                        sendJoinBuffer.WriteByte (ref sendJoinPos, (byte) OpCodes.DirectConnectIP);

                        if (Program.instance.NATConnections[clientId].Address.Equals (room.hostIP.Address))
                            sendJoinBuffer.WriteString (ref sendJoinPos, room.hostLocalIP == localIP ? "127.0.0.1" : room.hostLocalIP);
                        else
                            sendJoinBuffer.WriteString (ref sendJoinPos, room.hostIP.Address.ToString ());

                        sendJoinBuffer.WriteInt (ref sendJoinPos, room.useNATPunch ? room.hostIP.Port : room.port);
                        sendJoinBuffer.WriteBool (ref sendJoinPos, room.useNATPunch);

                        Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendJoinBuffer, 0, sendJoinPos), 0);

                        if (room.useNATPunch) {
                            sendJoinPos = 0;
                            sendJoinBuffer.WriteByte (ref sendJoinPos, (byte) OpCodes.DirectConnectIP);

                            sendJoinBuffer.WriteString (ref sendJoinPos, Program.instance.NATConnections[clientId].Address.ToString ());
                            sendJoinBuffer.WriteInt (ref sendJoinPos, Program.instance.NATConnections[clientId].Port);
                            sendJoinBuffer.WriteBool (ref sendJoinPos, true);

                            Program.transport.ServerSend (room.hostId, new ArraySegment<byte> (sendJoinBuffer, 0, sendJoinPos), 0);
                        }

                        _sendBuffers.Return (sendJoinBuffer);

                        Endpoint.RoomsModified ();
                        return;
                    } else {
                        sendJoinBuffer.WriteByte (ref sendJoinPos, (byte) OpCodes.ServerJoined);
                        sendJoinBuffer.WriteInt (ref sendJoinPos, clientId);

                        Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendJoinBuffer, 0, sendJoinPos), 0);
                        Program.transport.ServerSend (room.hostId, new ArraySegment<byte> (sendJoinBuffer, 0, sendJoinPos), 0);
                        _sendBuffers.Return (sendJoinBuffer);

                        Endpoint.RoomsModified ();
                        return;
                    }
                }
            }

            // If it got to here, then the server was not found, or full. Tell the client.
            int _pos = 0;
            byte[] _sendBuffer = _sendBuffers.Rent (1);

            _sendBuffer.WriteByte (ref _pos, (byte) OpCodes.ServerLeft);

            Program.transport.ServerSend (clientId, new ArraySegment<byte> (_sendBuffer, 0, _pos), 0);
            _sendBuffers.Return (_sendBuffer);
        }

        /// <summary>
        /// Makes the client leave their room.
        /// </summary>
        /// <param name="clientId">The client of which to remove from their room</param>
        /// <param name="requiredHostId">The ID of the client who kicked the client. -1 if the client left on their own terms</param>
        private void LeaveRoom (int clientId, int requiredHostId = -1) {
            for (int i = 0; i < rooms.Count; i++) {
                // if host left
                if (rooms[i].hostId == clientId) {
                    int pos = 0;
                    byte[] sendBuffer = _sendBuffers.Rent (1);
                    sendBuffer.WriteByte (ref pos, (byte) OpCodes.ServerLeft);

                    // Store the serverId before removing the room
                    string serverId = rooms[i].serverId; // Add this line

                    for (int x = 0; x < rooms[i].clients.Count; x++) {
                        Program.transport.ServerSend (rooms[i].clients[x], new ArraySegment<byte> (sendBuffer, 0, pos), 0);
                        _cachedClientRooms.Remove (rooms[i].clients[x]);
                    }

                    _sendBuffers.Return (sendBuffer);
                    rooms[i].clients.Clear ();
                    _cachedRooms.Remove (rooms[i].serverId);
                    rooms.RemoveAt (i);
                    _cachedClientRooms.Remove (clientId);
                    Endpoint.RoomsModified ();

                    // Use the stored serverId here
                    Program.WriteLogMessage ($"Client [{clientId}] [HOST LeaveRoom] [{serverId}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                    return;
                } else {
                    // if the person that tried to kick wasnt host and it wasnt the client leaving on their own
                    if (requiredHostId != -1 && rooms[i].hostId != requiredHostId)
                        continue;

                    if (rooms[i].clients.RemoveAll (x => x == clientId) > 0) {
                        int pos = 0;
                        byte[] sendBuffer = _sendBuffers.Rent (5);


                        sendBuffer.WriteByte (ref pos, (byte) OpCodes.PlayerDisconnected);
                        sendBuffer.WriteInt (ref pos, clientId);

                        Program.transport.ServerSend (rooms[i].hostId, new ArraySegment<byte> (sendBuffer, 0, pos), 0);
                        _sendBuffers.Return (sendBuffer);

                        // temporary solution to kicking bug
                        // this tells the local player that got kicked that he, well, got kicked.
                        pos = 0;
                        sendBuffer = _sendBuffers.Rent (1);

                        sendBuffer.WriteByte (ref pos, (byte) OpCodes.ServerLeft);

                        Program.transport.ServerSend (clientId, new ArraySegment<byte> (sendBuffer, 0, pos), 0);
                        _sendBuffers.Return (sendBuffer);

                        //end temporary solution

                        Endpoint.RoomsModified ();
                        _cachedClientRooms.Remove (clientId);
                        Program.WriteLogMessage ($"Client [{clientId}] [CLIENT LeaveRoom] [{rooms[i].serverId}] | Rooms [{string.Join (',', _cachedRooms.Keys)}]");
                    }
                }
            }
        }
    }
}