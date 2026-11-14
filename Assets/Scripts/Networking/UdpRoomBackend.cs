using PurrNet;
using PurrNet.Transports;
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace DontWakeIt.Networking
{
    public sealed class UdpRoomBackend : IRoomBackend
    {
        private const string Magic = "DWI1";

        private readonly ushort gamePort;
        private readonly int discoveryPort;
        private readonly float searchSeconds;

        private UDPTransport transport;
        private UdpClient listener;
        private string hostedCode;

        public UdpRoomBackend(ushort gamePort, int discoveryPort, float searchSeconds)
        {
            this.gamePort = gamePort;
            this.discoveryPort = discoveryPort;
            this.searchSeconds = searchSeconds;
        }

        public BackendKind Kind => BackendKind.Direct;
        public string DisplayName => "Direct / LAN";

        public bool IsAvailable(out string reason)
        {
            reason = null;
            return true;
        }

        public GenericTransport Bind(NetworkManager manager)
        {
            transport = manager.GetComponent<UDPTransport>();
            if (!transport) transport = manager.gameObject.AddComponent<UDPTransport>();
            return transport;
        }

        public bool PrepareHost(out string error)
        {
            transport.serverPort = gamePort;
            transport.address = "127.0.0.1";   // the host's own client connects to itself
            error = null;
            return true;
        }

        // ---------------------------------------------------------------- Hosting

        public IEnumerator Advertise(string code, int maxPlayers, RoomOperation operation)
        {
            StopAdvertising();

            string error = null;
            try
            {
                listener = new UdpClient(AddressFamily.InterNetwork);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                hostedCode = code;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (error != null)
            {
                StopAdvertising();
                operation.Fail("Couldn't open the room discovery port " + discoveryPort +
                               " (is another room already running on this PC?): " + error);
                yield break;
            }

            operation.Succeed(code);
        }

        public void StopAdvertising()
        {
            if (listener != null)
            {
                try { listener.Close(); } catch (Exception) { /* already closed */ }
                listener = null;
            }
            hostedCode = null;
        }

        /// <summary>Answers room-code queries while hosting.</summary>
        public void Tick()
        {
            if (listener == null || hostedCode == null) return;

            try
            {
                while (listener.Available > 0)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = listener.Receive(ref remote);
                    string message = Encoding.UTF8.GetString(data);
                    if (!message.StartsWith(Magic + "?", StringComparison.Ordinal)) continue;

                    string wanted = RoomCode.Normalize(message.Substring(Magic.Length + 1));
                    if (wanted != hostedCode) continue;

                    byte[] reply = Encoding.UTF8.GetBytes(Magic + "!" + hostedCode + ":" + gamePort);
                    listener.Send(reply, reply.Length, remote);
                }
            }
            catch (SocketException)
            {
                // Transient socket error while polling; try again next frame.
            }
            catch (ObjectDisposedException)
            {
                listener = null;
            }
        }

        public void Shutdown() => StopAdvertising();

        // ---------------------------------------------------------------- Joining

        public IEnumerator Resolve(string input, RoomOperation operation)
        {
            input = (input ?? string.Empty).Trim();

            if (LooksLikeAddress(input))
            {
                ParseAddress(input, out string host, out ushort port);
                transport.address = host;
                transport.serverPort = port;
                operation.Succeed(null);
                yield break;
            }

            string code = RoomCode.Normalize(input);
            if (!RoomCode.IsPlausible(code))
            {
                operation.Fail("Enter a room code (letters and numbers) or an IP address.");
                yield break;
            }

            UdpClient client = null;
            string error = null;
            try
            {
                client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                client.EnableBroadcast = true;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (error != null)
            {
                client?.Close();
                operation.Fail("Couldn't search the network: " + error);
                yield break;
            }

            byte[] query = Encoding.UTF8.GetBytes(Magic + "?" + code);
            float end = Time.unscaledTime + searchSeconds;
            float nextSend = 0f;

            while (Time.unscaledTime < end)
            {
                if (Time.unscaledTime >= nextSend)
                {
                    SendQuery(client, query);
                    nextSend = Time.unscaledTime + 0.5f;
                }

                if (TryReadReply(client, code, out IPAddress address, out ushort port))
                {
                    client.Close();
                    transport.address = address.ToString();
                    transport.serverPort = port;
                    operation.Succeed(code);
                    yield break;
                }

                yield return null;
            }

            client.Close();
            operation.Fail("No room with that code was found on this network. " +
                           "(Direct rooms are only found on the same network - or type the host's IP address.)");
        }

        private void SendQuery(UdpClient client, byte[] query)
        {
            // Broadcast for other machines on the LAN, loopback for another instance on this machine.
            TrySend(client, query, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
            TrySend(client, query, new IPEndPoint(IPAddress.Loopback, discoveryPort));
        }

        private static void TrySend(UdpClient client, byte[] data, IPEndPoint target)
        {
            try { client.Send(data, data.Length, target); }
            catch (SocketException) { /* no route (e.g. offline): the other target may still work */ }
            catch (ObjectDisposedException) { }
        }

        private static bool TryReadReply(UdpClient client, string code, out IPAddress address, out ushort port)
        {
            address = null;
            port = 0;
            try
            {
                while (client.Available > 0)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = client.Receive(ref remote);
                    string message = Encoding.UTF8.GetString(data);
                    if (!message.StartsWith(Magic + "!", StringComparison.Ordinal)) continue;

                    string[] parts = message.Substring(Magic.Length + 1).Split(':');
                    if (parts.Length != 2) continue;
                    if (!string.Equals(parts[0], code, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ushort.TryParse(parts[1], out port)) continue;

                    address = remote.Address;
                    return true;
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            return false;
        }

        private static bool LooksLikeAddress(string input)
        {
            return input.IndexOf('.') >= 0 || input.IndexOf(':') >= 0 ||
                   string.Equals(input, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private void ParseAddress(string input, out string host, out ushort port)
        {
            host = input;
            port = gamePort;

            int colon = input.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(input.Substring(colon + 1), out ushort parsed))
            {
                host = input.Substring(0, colon);
                port = parsed;
            }
        }
    }
}