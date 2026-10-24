using System;
using System.Buffers;
using System.Collections.Generic;

namespace LightReflectiveMirror
{
    public partial class RelayHandler
    {
        public List<Room> rooms = new();
        // connected clients that have not sent the auth key yet, and when they connected
        private Dictionary<int, DateTime> _pendingAuthentication = new();
        // only these clients get past HandleMessage's auth check. A connection
        // that is in neither set (rejected, or wrong key while its disconnect
        // is still in flight) is ignored instead of treated as authenticated.
        private HashSet<int> _authenticatedClients = new();
        private ArrayPool<byte> _sendBuffers;
        private int _maxPacketSize = 0;
        private Dictionary<int, Room> _cachedClientRooms = new();
        private Dictionary<string, Room> _cachedRooms = new();

        private System.Random _cachedRandom = new();

        // version and local IP have no legitimate reason to be long, and version is published on the server list
        private const int MaxShortFieldLength = 64;
    }

    public enum OpCodes
    {
        Default = 0, RequestID = 1, JoinServer = 2, SendData = 3, GetID = 4, ServerJoined = 5, GetData = 6, CreateRoom = 7, ServerLeft = 8, PlayerDisconnected = 9, RoomCreated = 10,
        LeaveRoom = 11, KickPlayer = 12, AuthenticationRequest = 13, AuthenticationResponse = 14, Authenticated = 17, UpdateRoomData = 18, ServerConnectionData = 19, RequestNATConnection = 20,
        DirectConnectIP = 21
    }
}
