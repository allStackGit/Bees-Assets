// The SteamManager is designed to work with Steamworks.NET
// This file is released into the public domain.
// Where that dedication is not recognized you are granted a perpetual,
// irrevocable license to copy and modify this file as you see fit.
//
// Version: 1.0.13

#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using Assets.Scripts.Levels;
using System;
using System.Collections.Generic;
using UnityEngine;
#if !DISABLESTEAMWORKS
using System.Collections;
using System.Runtime.InteropServices;
using Steamworks;
#endif

//
// The SteamManager provides a base implementation of Steamworks.NET on which you can build upon.
// It handles the basics of starting up and shutting down the SteamAPI for use.
//
[DisallowMultipleComponent]
public class SteamManager : MonoBehaviour {
#if !DISABLESTEAMWORKS
	protected static bool s_EverInitialized = false;

	protected static SteamManager s_instance;
	protected static SteamManager Instance {
		get {
			if (s_instance == null) {
				return new GameObject("SteamManager").AddComponent<SteamManager>();
			}
			else {
				return s_instance;
			}
		}
	}

	protected bool m_bInitialized = false;
	protected bool m_bInitializationFailed = false;
	public static bool Initialized {
		get {
			return Instance.m_bInitialized;
		}
	}

	/// <summary>
	/// True after this process attempted to initialize Steam and could not. Steam is an optional
	/// platform service for Bees startup; callers should fall back instead of blocking the game.
	/// </summary>
	public static bool InitializationFailed {
		get {
			return Instance.m_bInitializationFailed;
		}
	}

	protected SteamAPIWarningMessageHook_t m_SteamAPIWarningMessageHook;

	[AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
	protected static void SteamAPIDebugTextHook(int nSeverity, System.Text.StringBuilder pchDebugText) {
		Debug.LogWarning(pchDebugText);
	}

#if UNITY_2019_3_OR_NEWER
	// In case of disabled Domain Reload, reset static members before entering Play Mode.
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void InitOnPlayMode()
	{
		s_EverInitialized = false;
		s_instance = null;
	}
#endif

	private void MarkInitializationFailed(string reason, System.Exception exception = null)
	{
		m_bInitialized = false;
		m_bInitializationFailed = true;

		if (exception == null)
		{
			Debug.LogWarning($"[Steamworks.NET] {reason} Continuing without Steam features.", this);
		}
		else
		{
			Debug.LogWarning($"[Steamworks.NET] {reason} Continuing without Steam features. {exception.GetType().Name}: {exception.Message}", this);
		}
	}

	protected virtual void Awake() {
		// Only one instance of SteamManager at a time!
		if (s_instance != null) {
			Destroy(gameObject);
			return;
		}
		s_instance = this;

		if(s_EverInitialized) {
			// Steam is optional to the rest of the game. A late duplicate manager should not turn a
			// shutdown/lifecycle ordering issue into an uncaught exception that kills the player.
			MarkInitializationFailed("Tried to initialize the Steam API twice in one session.");
			return;
		}

		// We want our SteamManager Instance to persist across scenes.
		DontDestroyOnLoad(gameObject);

		try
		{
			if (!Packsize.Test()) {
				Debug.LogError("[Steamworks.NET] Packsize Test returned false, the wrong version of Steamworks.NET is being run in this platform.", this);
			}

			if (!DllCheck.Test()) {
				Debug.LogError("[Steamworks.NET] DllCheck Test returned false, One or more of the Steamworks binaries seems to be the wrong version.", this);
			}

			// If Steam is available and this title needs to be restarted through the Steam client,
			// preserve Steamworks.NET's normal restart behavior. Failure to load/initialize Steam,
			// however, is not fatal to Bees and is handled below.
			if (SteamAPI.RestartAppIfNecessary(AppId_t.Invalid)) {

				Application.Quit();
				return;
			}

			m_bInitialized = SteamAPI.Init();
		}
		catch (System.Exception exception)
		{
			MarkInitializationFailed("Steam could not be loaded or initialized.", exception);
			return;
		}

		if (!m_bInitialized) {
			MarkInitializationFailed("SteamAPI_Init() failed.");
			return;
		}

		m_bInitializationFailed = false;
		s_EverInitialized = true;
	}

	// This should only ever get called on first load and after an Assembly reload, You should never Disable the Steamworks Manager yourself.
	protected virtual void OnEnable() {
		if (s_instance == null) {
			s_instance = this;
		}

		if (!m_bInitialized) {
			return;
		}

		if (m_SteamAPIWarningMessageHook == null) {
			// Set up our callback to receive warning messages from Steam.
			// You must launch with "-debug_steamapi" in the launch args to receive warnings.
			m_SteamAPIWarningMessageHook = new SteamAPIWarningMessageHook_t(SteamAPIDebugTextHook);
			SteamClient.SetWarningMessageHook(m_SteamAPIWarningMessageHook);
		}
	}

	// OnApplicationQuit gets called too early to shutdown the SteamAPI.
	// Because the SteamManager should be persistent and never disabled or destroyed we can shutdown the SteamAPI here.
	// Thus it is not recommended to perform any Steamworks work in other OnDestroy functions as the order of execution can not be garenteed upon Shutdown. Prefer OnDisable().
	protected virtual void OnDestroy() {
		if (s_instance != this) {
			return;
		}

		s_instance = null;

		if (!m_bInitialized) {
			return;
		}

		SteamAPI.Shutdown();
	}

	protected virtual void Update() {
		if (!m_bInitialized) {
			return;
		}

		// Run Steam client callbacks
		SteamAPI.RunCallbacks();
	}
#else
	public static bool Initialized {
		get {
			return false;
		}
	}

	public static bool InitializationFailed {
		get {
			return true;
		}
	}
#endif // !DISABLESTEAMWORKS
}


public static class SteamMultiplayerTransportFactory
{
    public static IMultiplayerTransport Create(Stage stage, MatchSession session)
    {
#if !DISABLESTEAMWORKS
        if (stage == null || session == null || !session.HasRemotePeer || !SteamManager.Initialized)
        {
            return null;
        }

        SteamMultiplayerTransport transport = new SteamMultiplayerTransport(stage, session);
        if (!transport.IsAvailable)
        {
            transport.Dispose();
            return null;
        }
        return transport;
#else
        return null;
#endif
    }

    public static bool TryGetLocalTransportIdentity(out string transportIdentity)
    {
        transportIdentity = string.Empty;
#if !DISABLESTEAMWORKS
        if (!SteamManager.Initialized || !SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn())
        {
            return false;
        }

        SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
        identity.SetSteamID(SteamUser.GetSteamID());
        identity.ToString(out transportIdentity);
        return !string.IsNullOrWhiteSpace(transportIdentity);
#else
        return false;
#endif
    }
}

#if !DISABLESTEAMWORKS
public sealed class SteamMultiplayerTransport : IMultiplayerTransport
{
    private const int CommandChannel = 47;
    private const int MaxMessagesPerUpdate = 64;
    private const int ReceiveBatchSize = 16;
    private const int SendFlags =
        Constants.k_nSteamNetworkingSend_ReliableNoNagle |
        Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    private readonly Stage _stage;
    private readonly MatchSession _session;
    private readonly Dictionary<int, SteamNetworkingIdentity> _remoteIdentitiesByPeerId =
        new Dictionary<int, SteamNetworkingIdentity>();
    private readonly Dictionary<string, int> _peerIdsByTransportIdentity =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly IntPtr[] _receivePointers = new IntPtr[ReceiveBatchSize];
    private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private PlayerCommandEnvelope _pendingOutgoingCommand;
    private int _pendingOutgoingLevelId;
    private bool _sendFailureLogged;
    private bool _disposed;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable && !_disposed && SteamManager.Initialized;

    public SteamMultiplayerTransport(Stage stage, MatchSession session)
    {
        _stage = stage;
        _session = session;
        _isAvailable = BuildPeerIdentityMap();
        if (!_isAvailable)
        {
            return;
        }

        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
    }

    public void Update()
    {
        if (!IsAvailable)
        {
            return;
        }

        SendOutgoingCommands();
        ReceiveIncomingCommands();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionRequest?.Dispose();
        _sessionFailed?.Dispose();
        _sessionRequest = null;
        _sessionFailed = null;

        if (SteamManager.Initialized)
        {
            foreach (SteamNetworkingIdentity remoteIdentity in _remoteIdentitiesByPeerId.Values)
            {
                SteamNetworkingIdentity identity = remoteIdentity;
                try
                {
                    SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                }
                catch (Exception)
                {
                    // Steam may already be shutting down. Local match teardown still owns the
                    // managed transport lifetime, so there is nothing else to release here.
                }
            }
        }

        _remoteIdentitiesByPeerId.Clear();
        _peerIdsByTransportIdentity.Clear();
        _pendingOutgoingCommand = null;
        _pendingOutgoingLevelId = 0;
        _isAvailable = false;
    }

    private bool BuildPeerIdentityMap()
    {
        if (_stage == null || _session == null || !_session.HasRemotePeer)
        {
            return false;
        }

        IReadOnlyList<MatchPeer> peers = _session.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            MatchPeer peer = peers[i];
            if (peer.IsLocal)
            {
                continue;
            }

            string configuredIdentity = peer.TransportIdentity;
            if (string.IsNullOrWhiteSpace(configuredIdentity))
            {
                Debug.LogError($"Multiplayer peer {peer.Id} has no Steam transport identity.");
                return false;
            }

            SteamNetworkingIdentity steamIdentity = new SteamNetworkingIdentity();
            if (!steamIdentity.ParseString(configuredIdentity))
            {
                Debug.LogError($"Multiplayer peer {peer.Id} has an invalid Steam transport identity.");
                return false;
            }

            steamIdentity.ToString(out string canonicalIdentity);
            if (string.IsNullOrWhiteSpace(canonicalIdentity) ||
                _peerIdsByTransportIdentity.ContainsKey(canonicalIdentity))
            {
                Debug.LogError("Multiplayer Steam peer identities must be unique and canonical.");
                return false;
            }

            _remoteIdentitiesByPeerId.Add(peer.Id, steamIdentity);
            _peerIdsByTransportIdentity.Add(canonicalIdentity, peer.Id);
        }

        if (!_session.IsLocalAuthority &&
            !_remoteIdentitiesByPeerId.ContainsKey(_session.AuthorityPeerId))
        {
            Debug.LogError("The multiplayer authority peer has no usable Steam identity.");
            return false;
        }

        return _remoteIdentitiesByPeerId.Count > 0;
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        if (!TryResolvePeer(request.m_identityRemote, out _))
        {
            return;
        }

        SteamNetworkingIdentity identity = request.m_identityRemote;
        SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
    }

    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failure)
    {
        if (!TryResolvePeer(failure.m_info.m_identityRemote, out int peerId))
        {
            return;
        }

        Debug.LogWarning(
            $"Steam multiplayer session with peer {peerId} failed: " +
            $"{failure.m_info.m_eState} / {failure.m_info.m_eEndReason}.");
    }

    private bool TryResolvePeer(SteamNetworkingIdentity identity, out int peerId)
    {
        identity.ToString(out string transportIdentity);
        return _peerIdsByTransportIdentity.TryGetValue(transportIdentity, out peerId);
    }

    private void SendOutgoingCommands()
    {
        if (_session.IsLocalAuthority)
        {
            return;
        }
        if (!_remoteIdentitiesByPeerId.TryGetValue(
                _session.AuthorityPeerId,
                out SteamNetworkingIdentity authorityIdentity))
        {
            return;
        }

        int sent = 0;
        while (sent < MaxMessagesPerUpdate)
        {
            if (_pendingOutgoingCommand == null && !TryTakeNextOutgoingCommand())
            {
                break;
            }

            if (!MultiplayerProtocol.TrySerializeCommand(
                    _session.MatchId,
                    _pendingOutgoingLevelId,
                    _pendingOutgoingCommand,
                    out byte[] payload))
            {
                Debug.LogError("Dropping an invalid locally-issued multiplayer command packet.");
                _pendingOutgoingCommand = null;
                _pendingOutgoingLevelId = 0;
                continue;
            }

            EResult result = Send(authorityIdentity, payload);
            if (result != EResult.k_EResultOK)
            {
                if (!_sendFailureLogged)
                {
                    Debug.LogWarning($"Could not send multiplayer command to authority peer: {result}.");
                    _sendFailureLogged = true;
                }
                break;
            }

            _sendFailureLogged = false;
            _pendingOutgoingCommand = null;
            _pendingOutgoingLevelId = 0;
            sent++;
        }
    }

    private bool TryTakeNextOutgoingCommand()
    {
        List<Level> levels = _stage.Levels;
        for (int i = 0; i < levels.Count; i++)
        {
            Level level = levels[i];
            if (level != null &&
                level.State != null &&
                level.State.TryDequeueOutgoingPlayerCommand(out PlayerCommandEnvelope command))
            {
                _pendingOutgoingLevelId = level.State.MatchLevelId;
                _pendingOutgoingCommand = command;
                return true;
            }
        }

        return false;
    }

    private static EResult Send(SteamNetworkingIdentity identity, byte[] payload)
    {
        IntPtr data = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, data, payload.Length);
            return SteamNetworkingMessages.SendMessageToUser(
                ref identity,
                data,
                (uint)payload.Length,
                SendFlags,
                CommandChannel);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private void ReceiveIncomingCommands()
    {
        int receivedThisUpdate = 0;
        while (receivedThisUpdate < MaxMessagesPerUpdate)
        {
            int maxBatch = Math.Min(ReceiveBatchSize, MaxMessagesPerUpdate - receivedThisUpdate);
            int received;
            try
            {
                received = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                    CommandChannel,
                    _receivePointers,
                    maxBatch);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Steam multiplayer receive failed: {exception.GetType().Name}: {exception.Message}");
                return;
            }

            if (received <= 0)
            {
                return;
            }

            for (int i = 0; i < received; i++)
            {
                IntPtr pointer = _receivePointers[i];
                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    if (message.m_cbSize <= 0 ||
                        message.m_cbSize > MultiplayerProtocol.MaxPacketBytes ||
                        !TryResolvePeer(message.m_identityPeer, out int sourcePeerId))
                    {
                        continue;
                    }

                    byte[] payload = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, payload, 0, message.m_cbSize);
                    _stage.TryRouteReceivedPlayerCommandPacket(sourcePeerId, payload);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                    _receivePointers[i] = IntPtr.Zero;
                }
            }

            receivedThisUpdate += received;
        }
    }
}
#endif
