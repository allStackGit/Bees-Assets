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

            Socket socket = (Socket)FormatterServices.GetUninitializedObject(typeof(Socket));
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
            socket.StandingRequests = new StandingRequestSet();
            socket.HandledRequests = new HashSet<long>();
            socket.MessageQueue = new ConcurrentQueue<byte[]>();
            socket.OpenLevels = new List<Level>();

            socket.MakeSocket();
            return socket;
        }

        /// <summary>
        /// Creates the browser WebGL connection without invoking the legacy constructor, which
        /// would otherwise start an insecure ws:// connection before it could be replaced.
        /// NativeWebSocket is required in the browser; desktop/editor callers continue to use
        /// their existing WebSocketSharp path.
        /// </summary>
        internal static Socket CreateWebGl(int port, string hostname, string websocketUrl)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl) ||
                !Uri.TryCreate(websocketUrl, UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("WebGL WebSocket URL must be an absolute wss:// URL.", nameof(websocketUrl));
            }

            Socket socket = (Socket)FormatterServices.GetUninitializedObject(typeof(Socket));
            ConfigData.Stopwatch = System.Diagnostics.Stopwatch.StartNew();

            HostnameField.SetValue(socket, hostname);
            PortField.SetValue(socket, port);
            UseWebSocketSharpField.SetValue(socket, false);
            WebSocketUrlField.SetValue(socket, websocketUrl);

            socket.Protocol = "wss";
            socket.IsSecured = true;
            socket.IsOpen = false;
            socket.HasClosed = false;
            socket.KeepClosed = false;
            socket.StandingRequests = new StandingRequestSet();
            socket.HandledRequests = new HashSet<long>();
            socket.MessageQueue = new ConcurrentQueue<byte[]>();
            socket.OpenLevels = new List<Level>();

            socket.MakeSocket();
            return socket;
        }
    }
}
