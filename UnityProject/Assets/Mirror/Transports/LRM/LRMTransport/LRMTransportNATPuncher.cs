using Mirror;
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace LightReflectiveMirror
{
    public partial class LightReflectiveMirrorTransport : Transport
    {
        IEnumerator NATPunch(IPEndPoint remoteAddress)
        {
            for (int i = 0; i < 10; i++)
            {
                _NATPuncher.Send(_punchData, 1, remoteAddress);
                yield return new WaitForSeconds(0.25f);
            }
        }

        /// <summary>
        /// Arms the next NAT receive. Safe to call repeatedly; _natReceiveStarted
        /// tracks whether a receive is actually pending so a dead chain can be
        /// restarted by the next RequestNATConnection.
        /// </summary>
        void BeginNATReceive()
        {
            if (_NATPuncher == null)
            {
                _natReceiveStarted = false;
                return;
            }

            try
            {
                _NATPuncher.BeginReceive(new AsyncCallback(RecvData), _NATPuncher);
                _natReceiveStarted = true;
            }
            catch (ObjectDisposedException)
            {
                _natReceiveStarted = false;
            }
            catch (SocketException e)
            {
                _natReceiveStarted = false;
                Debug.LogError($"[LRM] Could not arm NAT receive: {e.SocketErrorCode}");
            }
        }

        void RecvData(IAsyncResult result)
        {
            byte[] data;
            IPEndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                data = _NATPuncher.EndReceive(result, ref newClientEP);
            }
            catch (ObjectDisposedException)
            {
                // Socket torn down underneath us. Leave the chain unarmed so the
                // next RequestNATConnection can restart it.
                _natReceiveStarted = false;
                return;
            }
            catch (SocketException)
            {
                // UDP reports an earlier send's ICMP Port Unreachable as a receive
                // error. Routine while punching at a peer that is not listening
                // yet, so keep the chain alive rather than letting it die.
                BeginNATReceive();
                return;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LRM] NAT receive failed: {e.Message}");
                BeginNATReceive();
                return;
            }

            BeginNATReceive();

            // Cannot tell whether this came from the relay if we have no puncher
            // endpoint yet, so ignore it rather than dereferencing null.
            if (_relayPuncherIP != null && !newClientEP.Address.Equals(_relayPuncherIP.Address))
            {
                if (_isServer)
                {
                    if (_serverProxies.TryGetByFirst(newClientEP, out SocketProxy foundProxy))
                    {
                        if (data.Length > 2)
                            foundProxy.RelayData(data, data.Length);
                    }
                    else
                    {
                        _serverProxies.Add(newClientEP, new SocketProxy(_NATIP.Port + 1, newClientEP));
                        _serverProxies.GetByFirst(newClientEP).dataReceived += ServerProcessProxyData;
                    }
                }

                if (_isClient)
                {
                    if (_clientProxy == null)
                    {
                        _clientProxy = new SocketProxy(_NATIP.Port - 1);
                        _clientProxy.dataReceived += ClientProcessProxyData;
                    }
                    else
                    {
                        _clientProxy.ClientRelayData(data, data.Length);
                    }
                }
            }
        }

        void ServerProcessProxyData(IPEndPoint remoteEndpoint, byte[] data)
        {
            _NATPuncher.Send(data, data.Length, remoteEndpoint);
        }

        void ClientProcessProxyData(IPEndPoint _, byte[] data)
        {
            _NATPuncher.Send(data, data.Length, _directConnectEndpoint);
        }
    }
}