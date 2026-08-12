using System;
using System.Text;
using Steamworks;
using UnityEngine;

namespace Assets.Scripts.Server
{
    internal static class SteamWebApiAuth
    {
        internal const string Identity = "bees-server";

        private static Callback<GetTicketForWebApiResponse_t> _callback;
        private static HAuthTicket _ticketHandle = HAuthTicket.Invalid;
        private static bool _requestPending;
        private static string _ticketHex;
        private static SteamAuthRetryPump _retryPump;

        internal static bool IsReady => !string.IsNullOrEmpty(_ticketHex);
        internal static string TicketHex => _ticketHex;

        internal static void EnsureRequested()
        {
            if (!ConfigData.Production || IsReady || _requestPending)
            {
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
                Debug.LogError($"Could not request BeesServer Steam authentication ticket: {exception.Message}");
            }
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
            ConfigData.LoadSettings();
        }

        internal static void Reset()
        {
            if (_ticketHandle != HAuthTicket.Invalid)
            {
                try
                {
                    SteamUser.CancelAuthTicket(_ticketHandle);
                }
                catch
                {
                }
            }
            _ticketHandle = HAuthTicket.Invalid;
            _ticketHex = null;
            _requestPending = false;
            if (_retryPump != null)
            {
                UnityEngine.Object.Destroy(_retryPump.gameObject);
                _retryPump = null;
            }
        }

        private sealed class SteamAuthRetryPump : MonoBehaviour
        {
            private float _nextAttempt;

            private void Update()
            {
                if (IsReady)
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
    }
}
