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
            int failures = 0;
            SocketError lastError = SocketError.Success;

            for (int i = 0; i < NAT_PUNCH_COROUTINE_ATTEMPTS; i++)
            {
                // A failed punch must not kill the coroutine. Unity runs the first
                // segment synchronously inside StartCoroutine, so an escaping
                // SocketException here surfaces as an unhandled exception and
                // abandons the remaining attempts.
                bool socketGone = _NATPuncher == null;

                if (!socketGone)
                {
                    try
                    {
                        _NATPuncher.Send(_punchData, 1, remoteAddress);
                    }
                    catch (SocketException e)
                    {
                        // Counted rather than logged per attempt; a peer that cannot
                        // be punched would otherwise emit one line per attempt.
                        failures++;
                        lastError = e.SocketErrorCode;
                    }
                    catch (ObjectDisposedException)
                    {
                        socketGone = true;
                    }
                }

                // yield cannot appear inside a catch clause, so bail out here.
                if (socketGone)
                    break;

                yield return new WaitForSeconds(NAT_PUNCH_COROUTINE_INTERVAL);
            }

            if (failures > 0)
                Debug.LogWarning($"[LRM] {failures}/{NAT_PUNCH_COROUTINE_ATTEMPTS} NAT punch attempts to {remoteAddress} failed, last error {lastError}.");
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

            // Snapshot: TeardownNATPuncher can null this from the main thread
            // between the callback firing and EndReceive running.
            UdpClient puncher = _NATPuncher;

            if (puncher == null)
            {
                _natReceiveStarted = false;
                return;
            }

            try
            {
                data = puncher.EndReceive(result, ref newClientEP);
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
            if (_relayPuncherIP == null || newClientEP.Address.Equals(_relayPuncherIP.Address))
                return;

            // Runs on a threadpool thread, so the main thread can null _NATIP
            // underneath us via TeardownNATPuncher. Snapshot it once; without
            // this the proxy ports below throw off-main-thread where Unity will
            // not surface the exception.
            IPEndPoint natIP = _NATIP;

            if (natIP == null)
                return;

            if (_isServer)
            {
                if (_serverProxies.TryGetByFirst(newClientEP, out SocketProxy foundProxy))
                {
                    if (data.Length > 2)
                        foundProxy.RelayData(data, data.Length);
                }
                else
                {
                    try
                    {
                        _serverProxies.Add(newClientEP, new SocketProxy(natIP.Port + 1, newClientEP));
                        _serverProxies.GetByFirst(newClientEP).dataReceived += ServerProcessProxyData;
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[LRM] Could not open a server proxy on port {natIP.Port + 1} for {newClientEP}: {e.Message}");
                    }
                }
            }

            if (_isClient)
            {
                if (_clientProxy == null)
                {
                    try
                    {
                        _clientProxy = new SocketProxy(natIP.Port - 1);
                        _clientProxy.dataReceived += ClientProcessProxyData;
                    }
                    catch (Exception e)
                    {
                        _clientProxy = null;
                        Debug.LogError($"[LRM] Could not open the client proxy on port {natIP.Port - 1}: {e.Message}");
                    }
                }
                else
                {
                    _clientProxy.ClientRelayData(data, data.Length);
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