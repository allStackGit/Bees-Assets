using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Assets.Scripts.Levels;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// The legacy Socket constructor connects immediately. For production, reproduce its small
    /// initialization sequence without invoking that constructor so the first network connection
    /// is WSS rather than briefly opening WS and racing its close callback against the secure one.
    /// </summary>
    internal static class SecureSocketFactory
    {
        private static readonly FieldInfo HostnameField = RequiredField("_hostname");
        private static readonly FieldInfo PortField = RequiredField("_port");
        private static readonly FieldInfo WebSocketUrlField = RequiredField("_websocketURL");
        private static readonly FieldInfo UseWebSocketSharpField = RequiredField("_useWebSocketSharp");

        private static FieldInfo RequiredField(string name)
        {
            FieldInfo field = typeof(Socket).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new MissingFieldException(typeof(Socket).FullName, name);
            }
            return field;
        }

        internal static Socket Create(int port, string hostname, bool useWebSocketSharp, bool secure)
        {
            if (!secure)
            {
                return new Socket(port, hostname, useWebSocketSharp);
            }
            if (!useWebSocketSharp)
            {
                throw new NotSupportedException("Production WSS currently requires the WebSocketSharp transport.");
            }

#pragma warning disable SYSLIB0050
            Socket socket = (Socket)FormatterServices.GetUninitializedObject(typeof(Socket));
#pragma warning restore SYSLIB0050
            ConfigData.Stopwatch = System.Diagnostics.Stopwatch.StartNew();

            HostnameField.SetValue(socket, hostname);
            PortField.SetValue(socket, port);
            UseWebSocketSharpField.SetValue(socket, true);
            WebSocketUrlField.SetValue(socket, $"wss://{hostname}:{port}");

            socket.Protocol = "wss";
            socket.IsSecured = true;
            socket.IsOpen = false;
            socket.HasClosed = false;
            socket.KeepClosed = false;
            socket.StandingRequests = new HashSet<ServerRequest>();
            socket.HandledRequests = new HashSet<long>();
            socket.MessageQueue = new ConcurrentQueue<byte[]>();
            socket.OpenLevels = new List<Level>();

            socket.MakeSocket();
            return socket;
        }
    }
}
