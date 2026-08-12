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

        internal static bool IsReady => !string.IsNullOrEmpty(_ticketHex);
        internal static string TicketHex => _ticketHex;

        internal static void EnsureRequested()
        {
            if (!ConfigData.Production || IsReady || _requestPending)
            {
                return;
            }

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
        }
    }
}
