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
        bool _clientInitialRecv = false;

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
            try
            {
                _udpClient.BeginReceive(new AsyncCallback(RecvData), _udpClient);
            }
            catch (ObjectDisposedException) { }
        }

        public void RelayData(byte[] data, int length)
        {
            _udpClient.Send(data, length);
            lastInteractionTime = DateTime.Now;
        }

        public void ClientRelayData(byte[] data, int length)
        {
            if (_clientInitialRecv)
            {
                _udpClient.Send(data, length, _recvEndpoint);
                lastInteractionTime = DateTime.Now;
            }
        }

        public void Dispose()
        {
            _udpClient.Dispose();
        }

        void RecvData(IAsyncResult result)
        {
            byte[] data;

            try
            {
                data = _udpClient.EndReceive(result, ref _recvEndpoint);
            }
            catch (ObjectDisposedException)
            {
                // Proxy was torn down while a receive was pending.
                return;
            }
            catch (SocketException)
            {
                // UDP surfaces an earlier send's ICMP Port Unreachable here.
                // Keep the chain alive rather than letting it die silently.
                Start();
                return;
            }

            Start();
            _clientInitialRecv = true;
            lastInteractionTime = DateTime.Now;
            dataReceived?.Invoke(_remoteEndpoint, data);
        }
    }
}