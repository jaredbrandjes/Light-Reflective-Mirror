using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace LightReflectiveMirror
{

    // This class handles the proxying from punched socket to transport.
    public class SocketProxy
    {
        public DateTime lastInteractionTime;
        public Action<IPEndPoint, byte[]> dataReceived;
        UdpClient _udpClient;
        IPEndPoint _recvEndpoint = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint _remoteEndpoint;
        volatile bool _clientInitialRecv = false;
        volatile bool _disposed = false;

        public bool IsDisposed => _disposed;

        // Receiving deliberately does NOT start in the constructors. The callers
        // wire up dataReceived afterwards, so a datagram arriving before that
        // would be delivered to a null handler - and, in the remote overload,
        // with a null _remoteEndpoint, which reaches ServerProcessProxyData and
        // throws inside a threadpool callback. Call Start() once wired.
        public SocketProxy(int port, IPEndPoint remoteEndpoint)
        {
            // Clone it so when main socket recvies new data, it wont switcheroo on us.
            _remoteEndpoint = new IPEndPoint(remoteEndpoint.Address, remoteEndpoint.Port);
            _udpClient = new UdpClient();
            _udpClient.Connect(new IPEndPoint(IPAddress.Loopback, port));
            lastInteractionTime = DateTime.Now;
        }

        public SocketProxy(int port)
        {
            _udpClient = new UdpClient(port);
            lastInteractionTime = DateTime.Now;
        }

        /// <summary>
        /// Begins receiving. Call after assigning dataReceived.
        /// </summary>
        public void Start()
        {
            if (_disposed)
                return;

            try
            {
                _udpClient.BeginReceive(new AsyncCallback(RecvData), _udpClient);
            }
            catch (ObjectDisposedException) { }
            catch (SocketException e)
            {
                // Callers run this from threadpool callbacks; never let it escape.
                Debug.LogWarning($"[LRM] Proxy could not arm its receive: {e.SocketErrorCode}");
            }
        }

        // Both relays run from threadpool callbacks and can race a Dispose on the
        // main thread, so neither may let a socket fault escape.
        public void RelayData(byte[] data, int length)
        {
            if (_disposed)
                return;

            try
            {
                _udpClient.Send(data, length);
                lastInteractionTime = DateTime.Now;
            }
            catch (ObjectDisposedException) { }
            catch (SocketException e)
            {
                Debug.LogWarning($"[LRM] Proxy relay failed: {e.SocketErrorCode}");
            }
        }

        public void ClientRelayData(byte[] data, int length)
        {
            if (_disposed || !_clientInitialRecv)
                return;

            // Snapshot: _recvEndpoint is rewritten by EndReceive on the receive thread.
            IPEndPoint target = _recvEndpoint;

            if (target == null)
                return;

            try
            {
                _udpClient.Send(data, length, target);
                lastInteractionTime = DateTime.Now;
            }
            catch (ObjectDisposedException) { }
            catch (SocketException e)
            {
                Debug.LogWarning($"[LRM] Proxy client relay to {target} failed: {e.SocketErrorCode}");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _udpClient.Dispose();
        }

        void RecvData(IAsyncResult result)
        {
            if (_disposed)
                return;

            byte[] data;

            // Per-callback local rather than the shared _recvEndpoint field. The
            // receive is re-armed below before the handler runs, so two callbacks
            // can be in here at once and a shared ref target would be rewritten
            // underneath whichever one is still using it.
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                data = _udpClient.EndReceive(result, ref from);
            }
            catch (ObjectDisposedException)
            {
                // Proxy was torn down while a receive was pending.
                return;
            }
            catch (SocketException)
            {
                // UDP surfaces an earlier send's ICMP Port Unreachable here, which
                // is routine while punching. Keep the chain alive, and count it as
                // interaction so an in-flight attempt is not swept as idle.
                lastInteractionTime = DateTime.Now;
                Start();
                return;
            }

            // Single reference assignment; ClientRelayData snapshots it.
            _recvEndpoint = from;
            _clientInitialRecv = true;
            lastInteractionTime = DateTime.Now;

            Start();
            dataReceived?.Invoke(_remoteEndpoint, data);
        }
    }
}