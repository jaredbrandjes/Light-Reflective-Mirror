using System;
using System.Net;
using System.Net.Sockets;

namespace LightReflectiveMirror
{
    partial class Program
    {
        void RunNATPunchLoop()
        {
            WriteLogMessage("OK\n", ConsoleColor.Green);
            IPEndPoint remoteEndpoint = new(IPAddress.Any, conf.NATPunchtroughPort);

            // Stock Data server sends to everyone:
            var serverResponse = new byte[1] { 1 };

            byte[] readData;
            bool isConnectionEstablished;
            int pos;
            string connectionID;

            while (true)
            {
                try
                {
                    readData = _punchServer.Receive(ref remoteEndpoint);
                }
                catch (SocketException)
                {
                    // Windows reports an ICMP port unreachable from an earlier Send here.
                    // Left uncaught it ended this thread and NAT punching with it.
                    continue;
                }

                pos = 0;
                try
                {
                    isConnectionEstablished = readData.ReadBool(ref pos, readData.Length);

                    if (isConnectionEstablished)
                    {
                        connectionID = readData.ReadString(ref pos, readData.Length);

                        // matched on the main loop, see ProcessNATPunches
                        _receivedNATPunches.Enqueue((connectionID, new IPEndPoint(remoteEndpoint.Address, remoteEndpoint.Port)));
                    }

                    _punchServer.Send(serverResponse, 1, remoteEndpoint);
                }
                catch
                {
                    // ignore, packet got fucked up or something.
                }
            }
        }

        /// <summary>
        /// Called from the main loop. Matches punches received by the NAT thread to their
        /// clients, so NATConnections and _pendingNATPunches are only touched on one thread.
        /// </summary>
        void ProcessNATPunches()
        {
            while (_receivedNATPunches.TryDequeue(out var punch))
            {
                if (_pendingNATPunches.TryGetBySecond(punch.natID, out int clientID))
                {
                    NATConnections[clientID] = punch.endpoint;
                    _pendingNATPunches.Remove(clientID);
                    Console.WriteLine("Client Successfully Established Puncher Connection. " + punch.endpoint.ToString());
                }
            }
        }
    }
}
