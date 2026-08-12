using System;
using System.Reflection;
using UnityEngine;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// The legacy Socket constructor builds and connects immediately. Keep that implementation
    /// intact, but for production close its bootstrap connection and replace it with WSS before
    /// the Socket instance is returned to any caller that can send application data.
    /// </summary>
    internal static class SecureSocketFactory
    {
        private static readonly FieldInfo WebSocketUrlField = typeof(Socket).GetField(
            "_websocketURL", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WebSocketSharpField = typeof(Socket).GetField(
            "_webSocketSharpSocket", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static Socket Create(int port, string hostname, bool useWebSocketSharp, bool secure)
        {
            Socket socket = new Socket(port, hostname, useWebSocketSharp);
            if (!secure)
            {
                return socket;
            }
            if (!useWebSocketSharp)
            {
                throw new NotSupportedException("Production WSS currently requires the WebSocketSharp transport.");
            }
            if (WebSocketUrlField == null || WebSocketSharpField == null)
            {
                throw new MissingFieldException("Socket transport internals changed; update SecureSocketFactory deliberately.");
            }

            WebSocketSharp.WebSocket bootstrapSocket = WebSocketSharpField.GetValue(socket) as WebSocketSharp.WebSocket;
            if (bootstrapSocket != null)
            {
                try
                {
                    bootstrapSocket.Close();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Could not close bootstrap WebSocket before enabling TLS: {exception.Message}");
                }
            }

            socket.Protocol = "wss";
            socket.IsSecured = true;
            socket.HasClosed = false;
            socket.KeepClosed = false;
            WebSocketUrlField.SetValue(socket, $"wss://{hostname}:{port}");
            socket.MakeSocket();
            return socket;
        }
    }
}
