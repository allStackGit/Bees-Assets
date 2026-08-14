// The SteamManager is designed to work with Steamworks.NET
// This file is released into the public domain.
// Where that dedication is not recognized you are granted a perpetual,
// irrevocable license to copy and modify this file as you see fit.
//
// Version: 1.0.13

#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using UnityEngine;
#if !DISABLESTEAMWORKS
using System.Collections;
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
				Debug.Log("[Steamworks.NET] Shutting down because RestartAppIfNecessary returned true. Steam will restart the application.");

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
