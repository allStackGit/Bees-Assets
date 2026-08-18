using System;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// Creates secure Socket instances without first opening an insecure connection.
    /// The secure Socket constructor runs normal field initialization before connecting,
    /// so request tracking, queues, and other runtime-owned state are always initialized.
    /// </summary>
    internal static class SecureSocketFactory
    {
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

            return new Socket(
                port,
                hostname,
                useWebSocketSharp: true,
                websocketUrl: $"wss://{hostname}:{port}",
                secured: true);
        }

        /// <summary>
        /// Creates the browser WebGL WSS connection with NativeWebSocket.
        /// </summary>
        internal static Socket CreateWebGl(int port, string hostname, string websocketUrl)
        {
            if (string.IsNullOrWhiteSpace(websocketUrl) ||
                !Uri.TryCreate(websocketUrl, UriKind.Absolute, out Uri uri) ||
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("WebGL WebSocket URL must be an absolute wss:// URL.", nameof(websocketUrl));
            }

            return new Socket(
                port,
                hostname,
                useWebSocketSharp: false,
                websocketUrl: websocketUrl,
                secured: true);
        }
    }
}
