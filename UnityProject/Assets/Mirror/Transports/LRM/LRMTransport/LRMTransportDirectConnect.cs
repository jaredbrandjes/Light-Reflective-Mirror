using Mirror;
using System;
using UnityEngine;

namespace LightReflectiveMirror
{
    public partial class LightReflectiveMirrorTransport : Transport
    {
        public void DirectAddClient(int clientID)
        {
            if (!_isServer)
                return;

            _connectedDirectClients.Add(clientID, _currentMemberId);
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
