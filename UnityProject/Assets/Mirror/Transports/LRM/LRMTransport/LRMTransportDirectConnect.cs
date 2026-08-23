using Mirror;
using System;
using UnityEngine;

namespace LightReflectiveMirror
{
    public partial class LightReflectiveMirrorTransport : Transport
    {
        public void DirectAddClient(int clientID, string clientAddress = null)
        {
            if (!_isServer)
                return;

            // Both OnServerConnected and OnServerConnectedWithAddress are wired on
            // the direct transport, since which one fires depends on the transport
            // and the Mirror version. Ignore the second call for the same client.
            if (_connectedDirectClients.TryGetByFirst(clientID, out int _))
                return;

            _connectedDirectClients.Add(clientID, _currentMemberId);

            // Punched clients reach us through a local SocketProxy, so the address
            // the transport reports is our own loopback and identifies nobody.
            // Keep DIRECT-<id> in that case, which at least distinguishes clients.
            if (!string.IsNullOrEmpty(clientAddress)
                && System.Net.IPAddress.TryParse(clientAddress, out System.Net.IPAddress parsed)
                && !System.Net.IPAddress.IsLoopback(parsed))
            {
                _directClientAddresses[_currentMemberId] = clientAddress;
            }

            // Deliberately raises only the obsolete callback: NetworkServer
            // subscribes to BOTH, so raising both would register the connection
            // twice.
            OnServerConnected?.Invoke(_currentMemberId);
            _currentMemberId++;
        }

        public void DirectRemoveClient(int clientID)
        {
            if (!_isServer)
                return;

            // DirectAddClient silently skips registration when we were not yet the
            // server, so a direct client can reach here unknown. GetByFirst would
            // throw KeyNotFoundException on that id.
            if (!_connectedDirectClients.TryGetByFirst(clientID, out int connectionId))
                return;

            OnServerDisconnected?.Invoke(connectionId);
            _connectedDirectClients.Remove(clientID);
            _directClientAddresses.Remove(connectionId);
        }

        public void DirectReceiveData(ArraySegment<byte> data, int channel, int clientID = -1)
        {
            if (_isServer)
            {
                // Same unregistered-client case as DirectRemoveClient: data can
                // arrive from a direct peer we never added, and an unguarded
                // lookup takes the whole server down with a KeyNotFoundException.
                if (_connectedDirectClients.TryGetByFirst(clientID, out int connectionId))
                    OnServerDataReceived?.Invoke(connectionId, data, channel);
                else
                    Debug.LogWarning($"[LRM] Dropped direct data from unregistered client {clientID}.");
            }

            if (_isClient)
                OnClientDataReceived?.Invoke(data, channel);
        }

        public void DirectClientConnected()
        {
            _directConnected = true;
            OnClientConnected?.Invoke();
        }

        public void DirectDisconnected()
        {
            if (_directConnected)
            {
                _isClient = false;
                _directConnected = false;
                OnClientDisconnected?.Invoke();
            }
            else if (!_intentionalDisconnect && !string.IsNullOrEmpty(_cachedHostID))
            {
                int pos = 0;
                _directConnected = false;
                _clientSendBuffer.WriteByte(ref pos, (byte)OpCodes.JoinServer);
                _clientSendBuffer.WriteString(ref pos, _cachedHostID);
                _clientSendBuffer.WriteBool(ref pos, false); // Direct failed, use relay
                _clientSendBuffer.WriteString(ref pos, GetLocalIp() ?? "0.0.0.0");

                _isClient = true;

                clientToServerTransport.ClientSend(new System.ArraySegment<byte>(_clientSendBuffer, 0, pos), 0);
            }

            if (_clientProxy != null)
            {
                _clientProxy.Dispose();
                _clientProxy = null;
            }
        }
    }
}
