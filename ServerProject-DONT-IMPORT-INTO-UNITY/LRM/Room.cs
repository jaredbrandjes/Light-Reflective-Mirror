using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;

namespace LightReflectiveMirror
{
    [JsonObject(MemberSerialization.OptOut)]
    public class Room
    {
        public string serverId;
        public int hostId;
        public string serverName;
        public string serverData;
        public bool isPublic;
        public int maxPlayers;

        public int appId;
        public string version;

        public int currentPlayers { get => clients.Count + 1; } // player count

        [JsonIgnore]
        public List<int> clients;

        public RelayAddress relayInfo;

        [JsonIgnore]
        public bool supportsDirectConnect = false;
        [JsonIgnore]
        public IPEndPoint hostIP;
        [JsonIgnore]
        public string hostLocalIP;
        [JsonIgnore]
        public bool useNATPunch = false;

        /// <summary>
        /// Host port for direct connections. The meaning depends on useNATPunch:
        /// when true this is the host's LOCAL NAT puncher port and its direct
        /// server sits at port + 1; when false it is the direct connect
        /// transport's own port. Hosts older than V16 report 0 while punching.
        /// Do not read this without checking useNATPunch.
        /// </summary>
        [JsonIgnore]
        public int port;
    }

    [Serializable]
    public struct RelayAddress
    {
        public ushort port;
        public ushort endpointPort;
        public string address;
        public LRMRegions serverRegion;
    }
}
