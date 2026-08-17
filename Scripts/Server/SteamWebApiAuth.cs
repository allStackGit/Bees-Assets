#if !UNITY_WEBGL
using System;
using System.Linq;
using System.Text;
using Steamworks;
#endif
using UnityEngine;

namespace Assets.Scripts.Server
{
    internal static class SteamWebApiAuth
    {
        internal const string Identity = "bees-server";

#if UNITY_WEBGL
        private static bool _loggedSteamUnavailable;

        internal static bool IsReady => false;
        internal static bool IsUnavailable => true;
        internal static string TicketHex => null;

        internal static void EnsureRequested()
        {
            if (_loggedSteamUnavailable)
            {
                return;
            }

            _loggedSteamUnavailable = true;
            Debug.LogWarning("Steam Web API authentication is unavailable on WebGL. Continuing without a Steam authentication ticket.");
        }

        internal static void Refresh()
        {
            EnsureRequested();
        }

        internal static void Reset()
        {
            _loggedSteamUnavailable = false;
        }
#else
        private static Callback<GetTicketForWebApiResponse_t> _callback;
        private static HAuthTicket _ticketHandle = HAuthTicket.Invalid;
        private static bool _requestPending;
        private static bool _steamUnavailable;
        private static bool _loggedSteamUnavailable;
        private static string _ticketHex;
        private static SteamAuthRetryPump _retryPump;

        internal static bool IsReady => !string.IsNullOrEmpty(_ticketHex);
        internal static bool IsUnavailable => _steamUnavailable;
        internal static string TicketHex => _ticketHex;

        internal static void EnsureRequested()
        {
            if (IsReady || _requestPending || _steamUnavailable)
            {
                return;
            }

            // Production requests acquire a ticket before being sent. Development normally uses
            // the insecure test server, but a 401 from any secured server can also call this method
            // and upgrade the existing standing requests without requiring a different client build.
            // SteamManager owns SteamAPI initialization; never call Steam user/auth APIs when it
            // failed to initialize.
            if (!SteamManager.Initialized)
            {
                _steamUnavailable = true;
                if (!_loggedSteamUnavailable)
                {
                    _loggedSteamUnavailable = true;
                    Debug.LogWarning("Steam Web API authentication is unavailable because Steam failed to initialize. Continuing without a Steam authentication ticket.");
                }
                StopRetryPump();
                return;
            }

            EnsureRetryPump();

            try
            {
                if (!SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn())
                {
                    return;
                }
                if (_callback == null)
                {
                    _callback = Callback<GetTicketForWebApiResponse_t>.Create(OnTicketReceived);
                }
                _ticketHandle = SteamUser.GetAuthTicketForWebApi(Identity);
                if (_ticketHandle == HAuthTicket.Invalid)
                {
                    Debug.LogError("Steam failed to create a BeesServer Web API authentication ticket.");
                    return;
                }
                _requestPending = true;
            }
            catch (Exception exception)
            {
                _steamUnavailable = true;
                Debug.LogWarning($"Could not request BeesServer Steam authentication ticket; continuing without Steam authentication. {exception.GetType().Name}: {exception.Message}");
                StopRetryPump();
            }
        }

        internal static void Refresh()
        {
            if (_steamUnavailable)
            {
                return;
            }

            // Several in-flight requests can receive the same 401. One rejected ticket should
            // produce one replacement request, not repeatedly cancel that replacement.
            if (_requestPending && !IsReady)
            {
                return;
            }

            Reset();
            EnsureRequested();
        }

        private static void EnsureRetryPump()
        {
            if (_retryPump != null)
            {
                return;
            }
            GameObject host = new GameObject("Steam Web API Auth Retry");
            UnityEngine.Object.DontDestroyOnLoad(host);
            _retryPump = host.AddComponent<SteamAuthRetryPump>();
        }

        private static void StopRetryPump()
        {
            if (_retryPump == null)
            {
                return;
            }

            UnityEngine.Object.Destroy(_retryPump.gameObject);
            _retryPump = null;
        }

        private static void OnTicketReceived(GetTicketForWebApiResponse_t response)
        {
            if (response.m_hAuthTicket != _ticketHandle)
            {
                return;
            }
            _requestPending = false;
            if (response.m_eResult != EResult.k_EResultOK || response.m_cubTicket <= 0 || response.m_rgubTicket == null)
            {
                Debug.LogError($"Steam returned {response.m_eResult} while creating a BeesServer authentication ticket.");
                return;
            }

            int length = Math.Min(response.m_cubTicket, response.m_rgubTicket.Length);
            StringBuilder builder = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
            {
                builder.Append(response.m_rgubTicket[i].ToString("x2"));
            }
            _ticketHex = builder.ToString();

            // A ticket can be requested proactively by Production or reactively after a 401 from a
            // secured development server. In either case rotate and resend every auth-bearing
            // standing request with the newly accepted credential.
            RefreshStandingAuthenticationRequests(_ticketHex);

            ConfigData.LoadSettings();
        }

        private static void RefreshStandingAuthenticationRequests(string ticket)
        {
            Socket socket = ConfigData.Socket;
            if (socket == null || !socket.IsOpen || string.IsNullOrEmpty(ticket))
            {
                return;
            }

            foreach (ServerRequest request in socket.StandingRequests
                         .Where(HasAuthenticationPayload)
                         .ToList())
            {
                // A retry after credential replacement is a new transport identity. Rotate the
                // hash so a delayed 401 from the rejected ticket cannot match the replacement
                // request and start another refresh cycle. Remove hash-based set membership before
                // mutation so those sets cannot be corrupted by a changed GetHashCode().
                long oldHash = request.Hash;
                socket.StandingRequests.Remove(request);
                ConfigData.__PastServerRequests.Remove(request);
                socket.HandledRequests.Remove(oldHash);

                request.Hash = Utilities.Hash();
                ApplyAuthenticationPayload(request, ticket);
                socket.SendRequest(request, true);
            }
        }

        private static bool HasAuthenticationPayload(ServerRequest request)
        {
            return request != null &&
                   (request.Type == ConfigData.RequestTypes.SetupLevel ||
                    request.Type == ConfigData.RequestTypes.ReconnectLevel ||
                    request.Type == ConfigData.RequestTypes.StoreUserData ||
                    request.Type == ConfigData.RequestTypes.GetUserData ||
                    request.Type == ConfigData.RequestTypes.GetSettings);
        }

        private static void ApplyAuthenticationPayload(ServerRequest request, string ticket)
        {
            switch (request.Type)
            {
                case ConfigData.RequestTypes.SetupLevel:
                    ((SetupLevelRequest)request).Request.AuthTicket = ticket;
                    ((SetupLevelRequest)request).Request.Hash = request.Hash;
                    break;
                case ConfigData.RequestTypes.ReconnectLevel:
                    ((ReconnectLevelRequest)request).Request.AuthTicket = ticket;
                    ((ReconnectLevelRequest)request).Request.Hash = request.Hash;
                    break;
                case ConfigData.RequestTypes.StoreUserData:
                    ((StoreUserDataRequest)request).Request.AuthTicket = ticket;
                    ((StoreUserDataRequest)request).Request.Hash = request.Hash;
                    break;
                case ConfigData.RequestTypes.GetUserData:
                    ((DataFileRequest)request).Request.AuthTicket = ticket;
                    ((DataFileRequest)request).Request.Hash = request.Hash;
                    break;
                case ConfigData.RequestTypes.GetSettings:
                    ((SettingsRequest)request).Request.AuthTicket = ticket;
                    ((SettingsRequest)request).Request.Hash = request.Hash;
                    break;
            }
        }

        internal static void Reset()
        {
            if (_ticketHandle != HAuthTicket.Invalid)
            {
                try
                {
                    if (SteamManager.Initialized)
                    {
                        SteamUser.CancelAuthTicket(_ticketHandle);
                    }
                }
                catch
                {
                }
            }
            _ticketHandle = HAuthTicket.Invalid;
            _ticketHex = null;
            _requestPending = false;
            _steamUnavailable = false;
            _loggedSteamUnavailable = false;
            StopRetryPump();
        }

        private sealed class SteamAuthRetryPump : MonoBehaviour
        {
            private float _nextAttempt;

            private void Update()
            {
                if (IsReady || IsUnavailable)
                {
                    Destroy(gameObject);
                    _retryPump = null;
                    return;
                }
                if (Time.unscaledTime < _nextAttempt)
                {
                    return;
                }
                _nextAttempt = Time.unscaledTime + 0.5f;
                EnsureRequested();
            }
        }
#endif
    }
}
