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


public static class SteamMultiplayerLobbyTransportFactory
{
    public static IMultiplayerLobbyTransport CreateHost(MatchSession session)
    {
#if !DISABLESTEAMWORKS
        if (session == null ||
            !session.IsConfiguring ||
            !session.IsLocalAuthority ||
            !session.HasRemotePeer ||
            !SteamManager.Initialized)
        {
            return null;
        }

        SteamMultiplayerLobbyTransport transport =
            SteamMultiplayerLobbyTransport.CreateHost(session);
        if (transport == null || !transport.IsAvailable)
        {
            transport?.Dispose();
            return null;
        }
        return transport;
#else
        return null;
#endif
    }

    public static IMultiplayerLobbyTransport CreateClient(string authorityTransportIdentity)
    {
#if !DISABLESTEAMWORKS
        if (string.IsNullOrWhiteSpace(authorityTransportIdentity) ||
            !SteamManager.Initialized ||
            !SteamMultiplayerTransportFactory.TryGetLocalTransportIdentity(
                out string localTransportIdentity))
        {
            return null;
        }

        SteamMultiplayerLobbyTransport transport =
            SteamMultiplayerLobbyTransport.CreateClient(
                authorityTransportIdentity,
                localTransportIdentity);
        if (transport == null || !transport.IsAvailable)
        {
            transport?.Dispose();
            return null;
        }
        return transport;
#else
        return null;
#endif
    }
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
public sealed class SteamMultiplayerLobbyTransport : IMultiplayerLobbyTransport
{
    private const int LobbyChannel = 46;
    private const int ReceiveBatchSize = 4;
    private const int MaxMessagesPerUpdate = 16;
    private const float BroadcastIntervalSeconds = 0.5f;
    private const int SendFlags =
        Constants.k_nSteamNetworkingSend_ReliableNoNagle |
        Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    private readonly bool _isHost;
    private readonly MatchSession _hostSession;
    private readonly string _localTransportIdentity;
    private readonly Dictionary<int, SteamNetworkingIdentity> _remoteIdentitiesByPeerId =
        new Dictionary<int, SteamNetworkingIdentity>();
    private readonly Dictionary<string, int> _allowedPeerIdsByTransportIdentity =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly IntPtr[] _receivePointers = new IntPtr[ReceiveBatchSize];
    private SteamNetworkingIdentity _authorityIdentity;
    private string _authorityCanonicalIdentity;
    private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private MatchSession _receivedSession;
    private float _nextBroadcastAt;
    private float _nextFailureLogAt;
    private bool _disposed;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable && !_disposed && SteamManager.Initialized;

    private SteamMultiplayerLobbyTransport(
        bool isHost,
        MatchSession hostSession,
        string localTransportIdentity)
    {
        _isHost = isHost;
        _hostSession = hostSession;
        _localTransportIdentity = localTransportIdentity ?? string.Empty;
    }

    public static SteamMultiplayerLobbyTransport CreateHost(MatchSession session)
    {
        if (!SteamMultiplayerTransportFactory.TryGetLocalTransportIdentity(
                out string localIdentity))
        {
            return null;
        }

        SteamMultiplayerLobbyTransport transport =
            new SteamMultiplayerLobbyTransport(true, session, localIdentity);
        if (!transport.ConfigureHost())
        {
            transport.Dispose();
            return null;
        }
        transport.RegisterCallbacks();
        transport._isAvailable = true;
        return transport;
    }

    public static SteamMultiplayerLobbyTransport CreateClient(
        string authorityTransportIdentity,
        string localTransportIdentity)
    {
        SteamMultiplayerLobbyTransport transport =
            new SteamMultiplayerLobbyTransport(false, null, localTransportIdentity);
        if (!transport.ConfigureClient(authorityTransportIdentity))
        {
            transport.Dispose();
            return null;
        }
        transport.RegisterCallbacks();
        transport._isAvailable = true;
        return transport;
    }

    public void Update()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (_isHost)
        {
            BroadcastLobbySnapshot();
        }
        else
        {
            ReceiveLobbySnapshots();
        }
    }

    public bool TryTakeReceivedSession(out MatchSession session)
    {
        if (_isHost || _receivedSession == null)
        {
            session = null;
            return false;
        }

        session = _receivedSession;
        _receivedSession = null;
        return true;
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
                TryCloseSession(ref identity);
            }

            if (!_isHost && !string.IsNullOrEmpty(_authorityCanonicalIdentity))
            {
                SteamNetworkingIdentity identity = _authorityIdentity;
                TryCloseSession(ref identity);
            }
        }

        _remoteIdentitiesByPeerId.Clear();
        _allowedPeerIdsByTransportIdentity.Clear();
        _receivedSession = null;
        _isAvailable = false;
    }

    private bool ConfigureHost()
    {
        if (_hostSession == null ||
            !_hostSession.IsConfiguring ||
            !_hostSession.IsLocalAuthority ||
            !_hostSession.HasRemotePeer)
        {
            return false;
        }

        IReadOnlyList<MatchPeer> peers = _hostSession.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            MatchPeer peer = peers[i];
            if (peer.IsLocal)
            {
                if (!string.Equals(
                        peer.TransportIdentity,
                        _localTransportIdentity,
                        StringComparison.Ordinal))
                {
                    return false;
                }
                continue;
            }

            if (!TryParseIdentity(
                    peer.TransportIdentity,
                    out SteamNetworkingIdentity identity,
                    out string canonicalIdentity) ||
                _allowedPeerIdsByTransportIdentity.ContainsKey(canonicalIdentity))
            {
                return false;
            }

            _remoteIdentitiesByPeerId.Add(peer.Id, identity);
            _allowedPeerIdsByTransportIdentity.Add(canonicalIdentity, peer.Id);
        }

        return _remoteIdentitiesByPeerId.Count > 0;
    }

    private bool ConfigureClient(string authorityTransportIdentity)
    {
        if (string.IsNullOrWhiteSpace(_localTransportIdentity) ||
            string.Equals(
                authorityTransportIdentity,
                _localTransportIdentity,
                StringComparison.Ordinal) ||
            !TryParseIdentity(
                authorityTransportIdentity,
                out _authorityIdentity,
                out _authorityCanonicalIdentity))
        {
            return false;
        }

        return true;
    }

    private void RegisterCallbacks()
    {
        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(
            OnSessionRequest);
        _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(
            OnSessionFailed);
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        if (!IsAllowedIdentity(request.m_identityRemote))
        {
            return;
        }

        SteamNetworkingIdentity identity = request.m_identityRemote;
        SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
    }

    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failure)
    {
        if (!IsAllowedIdentity(failure.m_info.m_identityRemote))
        {
            return;
        }

        if (Time.realtimeSinceStartup >= _nextFailureLogAt)
        {
            Debug.LogWarning(
                $"Steam multiplayer lobby session failed: " +
                $"{failure.m_info.m_eState} / {failure.m_info.m_eEndReason}.");
            _nextFailureLogAt = Time.realtimeSinceStartup + 2f;
        }
    }

    private bool IsAllowedIdentity(SteamNetworkingIdentity identity)
    {
        identity.ToString(out string canonicalIdentity);
        if (_isHost)
        {
            return _allowedPeerIdsByTransportIdentity.ContainsKey(canonicalIdentity);
        }

        return string.Equals(
            canonicalIdentity,
            _authorityCanonicalIdentity,
            StringComparison.Ordinal);
    }

    private void BroadcastLobbySnapshot()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextBroadcastAt)
        {
            return;
        }
        _nextBroadcastAt = now + BroadcastIntervalSeconds;

        if (!_hostSession.TryCreateLobbySnapshot(out MatchLobbySnapshot snapshot) ||
            !MultiplayerProtocol.TrySerializeLobbySnapshot(snapshot, out byte[] payload))
        {
            return;
        }

        foreach (KeyValuePair<int, SteamNetworkingIdentity> peer in _remoteIdentitiesByPeerId)
        {
            SteamNetworkingIdentity identity = peer.Value;
            EResult result = Send(identity, payload);
            if (result != EResult.k_EResultOK && now >= _nextFailureLogAt)
            {
                Debug.LogWarning(
                    $"Could not send multiplayer lobby snapshot to peer {peer.Key}: {result}.");
                _nextFailureLogAt = now + 2f;
            }
        }
    }

    private void ReceiveLobbySnapshots()
    {
        int maxBatches = MaxMessagesPerUpdate / ReceiveBatchSize;
        for (int batch = 0; batch < maxBatches; batch++)
        {
            int received;
            try
            {
                received = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                    LobbyChannel,
                    _receivePointers,
                    ReceiveBatchSize);
            }
            catch (Exception exception)
            {
                if (Time.realtimeSinceStartup >= _nextFailureLogAt)
                {
                    Debug.LogWarning(
                        $"Steam multiplayer lobby receive failed: " +
                        $"{exception.GetType().Name}: {exception.Message}");
                    _nextFailureLogAt = Time.realtimeSinceStartup + 2f;
                }
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
                    SteamNetworkingMessage_t message =
                        SteamNetworkingMessage_t.FromIntPtr(pointer);
                    if (message.m_cbSize <= 0 ||
                        message.m_cbSize > MultiplayerProtocol.MaxLobbyPacketBytes ||
                        !IsAllowedIdentity(message.m_identityPeer))
                    {
                        continue;
                    }

                    byte[] payload = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, payload, 0, message.m_cbSize);
                    if (MultiplayerProtocol.TryDeserializeLobbySnapshot(
                            payload,
                            out MatchLobbySnapshot snapshot) &&
                        MatchSession.TryCreateFromLobbySnapshot(
                            snapshot,
                            _localTransportIdentity,
                            out MatchSession session))
                    {
                        _receivedSession = session;
                    }
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                    _receivePointers[i] = IntPtr.Zero;
                }
            }

            if (received < ReceiveBatchSize)
            {
                return;
            }
        }
    }

    private static bool TryParseIdentity(
        string transportIdentity,
        out SteamNetworkingIdentity identity,
        out string canonicalIdentity)
    {
        identity = new SteamNetworkingIdentity();
        canonicalIdentity = string.Empty;
        if (string.IsNullOrWhiteSpace(transportIdentity) ||
            !identity.ParseString(transportIdentity))
        {
            return false;
        }

        identity.ToString(out canonicalIdentity);
        return !string.IsNullOrWhiteSpace(canonicalIdentity);
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
                LobbyChannel);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static void TryCloseSession(ref SteamNetworkingIdentity identity)
    {
        try
        {
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }
        catch (Exception)
        {
            // Steam may already be shutting down.
        }
    }
}
#endif

#if !DISABLESTEAMWORKS
public sealed class SteamMultiplayerTransport : IMultiplayerTransport
{
    private const int CommandChannel = 47;
    private const int StateChannel = 48;
    private const int MaxMessagesPerUpdate = 64;
    private const int ReceiveBatchSize = 16;
    private const float CommandResendIntervalSeconds = 0.5f;
    private const float StateBroadcastIntervalSeconds = 0.1f;
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
    private readonly List<(int MatchLevelId, PlayerCommandEnvelope Command)> _outgoingCommandBuffer =
        new List<(int MatchLevelId, PlayerCommandEnvelope Command)>();
    private readonly Dictionary<(int PlayerId, long Sequence), float> _lastCommandSendTimes =
        new Dictionary<(int PlayerId, long Sequence), float>();
    private readonly HashSet<(int PlayerId, long Sequence)> _pendingCommandKeys =
        new HashSet<(int PlayerId, long Sequence)>();
    private readonly List<(int PlayerId, long Sequence)> _acknowledgedSendKeys =
        new List<(int PlayerId, long Sequence)>();
    private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
    private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;
    private bool _hasPendingAcknowledgement;
    private int _pendingAcknowledgementTargetPeerId;
    private int _pendingAcknowledgementPlayerId;
    private long _pendingAcknowledgementSequence;
    private bool _sendFailureLogged;
    private float _nextStateBroadcastAt;
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

        SendOutgoingAcknowledgements();
        SendOutgoingCommands();
        SendAuthoritativeBattleStates();
        ReceiveIncomingCommands();
        ReceiveBattleStates();
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
        _outgoingCommandBuffer.Clear();
        _lastCommandSendTimes.Clear();
        _pendingCommandKeys.Clear();
        _acknowledgedSendKeys.Clear();
        _hasPendingAcknowledgement = false;
        _pendingAcknowledgementTargetPeerId = 0;
        _pendingAcknowledgementPlayerId = MatchSession.UnownedPlayerId;
        _pendingAcknowledgementSequence = 0;
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

    private void SendAuthoritativeBattleStates()
    {
        if (!_session.IsLocalAuthority || _stage == null || _stage.Levels == null)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextStateBroadcastAt)
        {
            return;
        }
        _nextStateBroadcastAt = now + StateBroadcastIntervalSeconds;

        for (int levelIndex = 0; levelIndex < _stage.Levels.Count; levelIndex++)
        {
            Level level = _stage.Levels[levelIndex];
            if (level == null ||
                level.State == null ||
                !level.State.TryCreateAuthoritativeBattleStateSnapshot(
                    out BattleStateSnapshot snapshot) ||
                !MultiplayerProtocol.TrySerializeBattleState(
                    _session.MatchId,
                    snapshot,
                    out byte[] payload))
            {
                continue;
            }

            foreach (KeyValuePair<int, SteamNetworkingIdentity> peer in
                _remoteIdentitiesByPeerId)
            {
                EResult result = SendState(peer.Value, payload);
                if (result != EResult.k_EResultOK)
                {
                    if (!_sendFailureLogged)
                    {
                        Debug.LogWarning(
                            $"Could not send multiplayer battle state to peer {peer.Key}: {result}.");
                        _sendFailureLogged = true;
                    }
                    break;
                }

                _sendFailureLogged = false;
            }
        }
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

        _session.CopyOutgoingPlayerCommands(
            _outgoingCommandBuffer,
            MatchSession.MaxOutgoingPlayerCommands);
        PruneCommandSendTimes();
        float now = Time.realtimeSinceStartup;
        int sent = 0;
        for (int i = 0; i < _outgoingCommandBuffer.Count && sent < MaxMessagesPerUpdate; i++)
        {
            (int MatchLevelId, PlayerCommandEnvelope Command) queued = _outgoingCommandBuffer[i];
            PlayerCommandEnvelope command = queued.Command;
            if (command == null)
            {
                continue;
            }

            (int PlayerId, long Sequence) key = (command.PlayerId, command.Sequence);
            if (_lastCommandSendTimes.TryGetValue(key, out float lastSentAt) &&
                now - lastSentAt < CommandResendIntervalSeconds)
            {
                continue;
            }

            if (!MultiplayerProtocol.TrySerializeCommand(
                    _session.MatchId,
                    queued.MatchLevelId,
                    command,
                    out byte[] payload))
            {
                Debug.LogError(
                    $"Could not serialize multiplayer command {command.PlayerId}:{command.Sequence}.");
                _lastCommandSendTimes[key] = now;
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
            _lastCommandSendTimes[key] = now;
            sent++;
        }
    }

    private void PruneCommandSendTimes()
    {
        _pendingCommandKeys.Clear();
        for (int i = 0; i < _outgoingCommandBuffer.Count; i++)
        {
            PlayerCommandEnvelope command = _outgoingCommandBuffer[i].Command;
            if (command != null)
            {
                _pendingCommandKeys.Add((command.PlayerId, command.Sequence));
            }
        }

        _acknowledgedSendKeys.Clear();
        foreach (KeyValuePair<(int PlayerId, long Sequence), float> sent in _lastCommandSendTimes)
        {
            if (!_pendingCommandKeys.Contains(sent.Key))
            {
                _acknowledgedSendKeys.Add(sent.Key);
            }
        }
        for (int i = 0; i < _acknowledgedSendKeys.Count; i++)
        {
            _lastCommandSendTimes.Remove(_acknowledgedSendKeys[i]);
        }
        _acknowledgedSendKeys.Clear();
        _pendingCommandKeys.Clear();
    }

    private void SendOutgoingAcknowledgements()
    {
        if (!_session.IsLocalAuthority)
        {
            return;
        }

        int sent = 0;
        while (sent < MaxMessagesPerUpdate)
        {
            if (!_hasPendingAcknowledgement)
            {
                if (!_session.TryDequeuePlayerCommandAcknowledgement(
                        out _pendingAcknowledgementTargetPeerId,
                        out _pendingAcknowledgementPlayerId,
                        out _pendingAcknowledgementSequence))
                {
                    return;
                }
                _hasPendingAcknowledgement = true;
            }

            if (!_remoteIdentitiesByPeerId.TryGetValue(
                    _pendingAcknowledgementTargetPeerId,
                    out SteamNetworkingIdentity remoteIdentity))
            {
                Debug.LogError(
                    $"Cannot acknowledge multiplayer command for unknown peer {_pendingAcknowledgementTargetPeerId}.");
                ClearPendingAcknowledgement();
                continue;
            }

            if (!MultiplayerProtocol.TrySerializeAcknowledgement(
                    _session.MatchId,
                    _pendingAcknowledgementPlayerId,
                    _pendingAcknowledgementSequence,
                    out byte[] payload))
            {
                Debug.LogError("Could not serialize multiplayer command acknowledgement.");
                ClearPendingAcknowledgement();
                continue;
            }

            EResult result = Send(remoteIdentity, payload);
            if (result != EResult.k_EResultOK)
            {
                if (!_sendFailureLogged)
                {
                    Debug.LogWarning($"Could not send multiplayer command acknowledgement: {result}.");
                    _sendFailureLogged = true;
                }
                return;
            }

            _sendFailureLogged = false;
            ClearPendingAcknowledgement();
            sent++;
        }
    }

    private void ClearPendingAcknowledgement()
    {
        _hasPendingAcknowledgement = false;
        _pendingAcknowledgementTargetPeerId = 0;
        _pendingAcknowledgementPlayerId = MatchSession.UnownedPlayerId;
        _pendingAcknowledgementSequence = 0;
    }

    private void ApplyAcknowledgement(int playerId, long sequence)
    {
        if (_session.IsLocalAuthority || !_session.IsLocalPlayer(playerId))
        {
            return;
        }

        _session.AcknowledgeOutgoingPlayerCommands(playerId, sequence);

        _acknowledgedSendKeys.Clear();
        foreach (KeyValuePair<(int PlayerId, long Sequence), float> sent in _lastCommandSendTimes)
        {
            if (sent.Key.PlayerId == playerId && sent.Key.Sequence <= sequence)
            {
                _acknowledgedSendKeys.Add(sent.Key);
            }
        }
        for (int i = 0; i < _acknowledgedSendKeys.Count; i++)
        {
            _lastCommandSendTimes.Remove(_acknowledgedSendKeys[i]);
        }
        _acknowledgedSendKeys.Clear();
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

    private static EResult SendState(
        SteamNetworkingIdentity identity,
        byte[] payload)
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
                StateChannel);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private void ReceiveBattleStates()
    {
        if (_session.IsLocalAuthority)
        {
            return;
        }

        int maxBatches = MaxMessagesPerUpdate / ReceiveBatchSize;
        for (int batch = 0; batch < maxBatches; batch++)
        {
            int received;
            try
            {
                received = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                    StateChannel,
                    _receivePointers,
                    ReceiveBatchSize);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"Steam multiplayer state receive failed: " +
                    $"{exception.GetType().Name}: {exception.Message}");
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
                    SteamNetworkingMessage_t message =
                        SteamNetworkingMessage_t.FromIntPtr(pointer);
                    if (message.m_cbSize <= 0 ||
                        message.m_cbSize > MultiplayerProtocol.MaxBattleStatePacketBytes ||
                        !TryResolvePeer(message.m_identityPeer, out int sourcePeerId) ||
                        sourcePeerId != _session.AuthorityPeerId)
                    {
                        continue;
                    }

                    byte[] payload = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, payload, 0, message.m_cbSize);
                    _stage.TryRouteReceivedBattleStatePacket(
                        sourcePeerId,
                        payload);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                    _receivePointers[i] = IntPtr.Zero;
                }
            }

            if (received < ReceiveBatchSize)
            {
                return;
            }
        }
    }

    private void ReceiveIncomingCommands()
    {
        int maxBatches = MaxMessagesPerUpdate / ReceiveBatchSize;
        for (int batch = 0; batch < maxBatches; batch++)
        {
            int received;
            try
            {
                received = SteamNetworkingMessages.ReceiveMessagesOnChannel(
                    CommandChannel,
                    _receivePointers,
                    ReceiveBatchSize);
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

                    if (MultiplayerProtocol.TryDeserializeAcknowledgement(
                            payload,
                            _session.MatchId,
                            out int acknowledgedPlayerId,
                            out long acknowledgedSequence))
                    {
                        if (!_session.IsLocalAuthority &&
                            sourcePeerId == _session.AuthorityPeerId)
                        {
                            ApplyAcknowledgement(acknowledgedPlayerId, acknowledgedSequence);
                        }
                        continue;
                    }

                    _stage.TryRouteReceivedPlayerCommandPacket(sourcePeerId, payload);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                    _receivePointers[i] = IntPtr.Zero;
                }
            }

            if (received < ReceiveBatchSize)
            {
                return;
            }
        }
    }
}
#endif
