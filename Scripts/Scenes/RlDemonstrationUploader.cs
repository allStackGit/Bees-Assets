using Assets.Scripts;
using Assets.Scripts.Server;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using WebSocketSharp;

/// <summary>
/// Optional public-client transport for closed Human ML-Agents demonstrations.
///
/// This path is deliberately separate from the gameplay Socket: uploads are background telemetry,
/// not level/request ownership. The uploader snapshots only files that already existed before the
/// current scene begins recording, so it never reads an active DemonstrationRecorder file. Server
/// responses are quarantined and parsed again by the central trainer before any data is eligible for
/// imitation learning.
/// </summary>
internal sealed class RlDemonstrationUploader : MonoBehaviour
{
    internal const string UploadCommandLineFlag = "--rl-upload-demonstrations";
    internal const int MaxUploadBundleBytes = 16 * 1024 * 1024;
    internal const int DefaultChunkBytes = 512 * 1024;

    private const float AuthenticationWaitSeconds = 60f;
    private const float ConnectionWaitSeconds = 15f;
    private const float ResponseWaitSeconds = 15f;
    private const int MaxRequestAttempts = 3;

    [Serializable]
    private sealed class CaptureManifest
    {
        public int schemaVersion;
        public string behaviorName;
        public int policyAbiVersion;
        public string policySignature;
        public int observationSize;
        public int continuousActionCount;
        public int[] discreteBranchSizes;
    }

    private abstract class UploadRequest
    {
        public string Type;
        public long Hash;
        public string UserId;
        public string AuthTicket;
    }

    private sealed class BeginUploadRequest : UploadRequest
    {
        public string Source;
        public string DemonstrationId;
        public string GameBuildVersion;
        public long TotalBytes;
        public string DemoSha256;
        public string ManifestSha256;
        public string ManifestJson;
    }

    private sealed class ChunkUploadRequest : UploadRequest
    {
        public string UploadId;
        public long Offset;
        public string Data;
    }

    private sealed class CompleteUploadRequest : UploadRequest
    {
        public string UploadId;
    }

    private sealed class UploadResponse
    {
        public string Type;
        public long Hash;
        public int Status;
        public string ErrorCode;
        public string UploadId;
        public string BatchId;
        public bool Completed;
        public bool Duplicate;
        public long NextOffset;
        public int ChunkBytes;
    }

    private static bool _installed;

    private readonly List<string> _candidateFiles = new List<string>();
    private readonly ConcurrentQueue<string> _responses = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _transportErrors = new ConcurrentQueue<string>();

    private WebSocket _socket;
    private volatile bool _socketOpen;
    private volatile bool _socketClosed;
    private string _manifestJson;
    private string _manifestSha256;
    private string _userId;
    private string _gameBuildVersion;
    private UploadResponse _lastResponse;
    private bool _haltUploads;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        if (_installed || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            !IsUploadRequested(Environment.GetCommandLineArgs()))
        {
            return;
        }

        _installed = true;
        GameObject host = new GameObject("RL Demonstration Uploader");
        DontDestroyOnLoad(host);
        host.AddComponent<RlDemonstrationUploader>();
    }

    private void Awake()
    {
#if UNITY_WEBGL
        Debug.LogWarning("RL demonstration upload is unavailable on WebGL because Steam Web API authentication is unavailable.");
        Destroy(gameObject);
        return;
#else
        if (!ConfigData.Production)
        {
            Debug.LogWarning("RL demonstration upload is enabled only for authenticated Production connections.");
            Destroy(gameObject);
            return;
        }

        try
        {
            RlPolicySchema.ValidateOrThrow();
            SnapshotClosedHumanDemonstrations(
                GetPolicyCaptureRoot(),
                _candidateFiles,
                out _manifestJson,
                out _manifestSha256);
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"RL demonstration upload disabled while snapshotting closed captures: " +
                $"{exception.GetType().Name}: {exception.Message}");
            _candidateFiles.Clear();
        }
#endif
    }

    private void Start()
    {
#if !UNITY_WEBGL
        if (_candidateFiles.Count == 0)
        {
            Destroy(gameObject);
            return;
        }
        StartCoroutine(UploadSnapshot());
#endif
    }

    private void OnDestroy()
    {
#if !UNITY_WEBGL
        if (_socket != null)
        {
            try
            {
                _socket.CloseAsync();
            }
            catch
            {
                // Destruction is already terminal for this optional background transport.
            }
            _socket = null;
        }
#endif
    }

    internal static bool IsUploadRequested(IReadOnlyList<string> args)
    {
        if (args == null)
        {
            return false;
        }
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], UploadCommandLineFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string GetPolicyCaptureRoot()
    {
        return Path.Combine(
            Application.persistentDataPath,
            RlGameplayDemonstrationAgent.DemonstrationDirectoryName,
            $"PolicyV{RlPolicySchema.Version}");
    }

    internal static string BuildDemonstrationId(string demoSha256)
    {
        if (string.IsNullOrEmpty(demoSha256) || demoSha256.Length < 24)
        {
            throw new ArgumentException("A SHA-256 demonstration hash is required.", nameof(demoSha256));
        }
        return $"v{RlPolicySchema.Version}-{demoSha256.Substring(0, 24).ToLowerInvariant()}";
    }

    internal static bool IsUploadBundleWithinLimit(long demoBytes, string manifestJson)
    {
        return demoBytes > 0 && !string.IsNullOrEmpty(manifestJson) &&
               demoBytes + Encoding.UTF8.GetByteCount(manifestJson) <= MaxUploadBundleBytes;
    }

    internal static bool CaptureManifestMatchesCurrentPolicy(string manifestJson)
    {
        if (string.IsNullOrEmpty(manifestJson))
        {
            return false;
        }
        CaptureManifest manifest;
        try
        {
            manifest = JsonUtility.FromJson<CaptureManifest>(manifestJson);
        }
        catch
        {
            return false;
        }
        if (manifest == null || manifest.schemaVersion != 1 ||
            !string.Equals(manifest.behaviorName, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) ||
            manifest.policyAbiVersion != RlPolicySchema.Version ||
            !string.Equals(manifest.policySignature, RlPolicySchema.Signature, StringComparison.Ordinal) ||
            manifest.observationSize != RlPolicySchema.ExpectedObservationSize ||
            manifest.continuousActionCount != RlPolicySchema.ExpectedContinuousActions)
        {
            return false;
        }

        int[] expected = RlOneVsOneAgent.CreateDiscreteBranchSizes();
        if (manifest.discreteBranchSizes == null || manifest.discreteBranchSizes.Length != expected.Length)
        {
            return false;
        }
        for (int i = 0; i < expected.Length; i++)
        {
            if (manifest.discreteBranchSizes[i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    internal static void SnapshotClosedHumanDemonstrations(
        string policyRoot,
        List<string> destination,
        out string manifestJson,
        out string manifestSha256)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }
        destination.Clear();
        manifestJson = null;
        manifestSha256 = null;
        if (string.IsNullOrEmpty(policyRoot) || !Directory.Exists(policyRoot))
        {
            return;
        }

        string manifestPath = Path.Combine(
            policyRoot,
            RlGameplayDemonstrationAgent.CaptureManifestFileName);
        string humanDirectory = Path.Combine(policyRoot, "Human");
        if (!File.Exists(manifestPath) || !Directory.Exists(humanDirectory))
        {
            return;
        }

        manifestJson = File.ReadAllText(manifestPath);
        if (!CaptureManifestMatchesCurrentPolicy(manifestJson))
        {
            throw new InvalidDataException(
                $"Capture manifest {manifestPath} does not match RL policy ABI v{RlPolicySchema.Version}.");
        }
        manifestSha256 = ComputeSha256(Encoding.UTF8.GetBytes(manifestJson));

        foreach (string file in Directory.GetFiles(humanDirectory, "*.demo", SearchOption.TopDirectoryOnly)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            FileInfo info = new FileInfo(file);
            if (IsUploadBundleWithinLimit(info.Length, manifestJson))
            {
                destination.Add(file);
            }
        }
    }

    private IEnumerator UploadSnapshot()
    {
        SteamWebApiAuth.EnsureRequested();
        float authDeadline = Time.realtimeSinceStartup + AuthenticationWaitSeconds;
        while (!SteamWebApiAuth.IsReady && !SteamWebApiAuth.IsUnavailable &&
               Time.realtimeSinceStartup < authDeadline)
        {
            yield return null;
        }
        if (!SteamWebApiAuth.IsReady)
        {
            Debug.LogWarning("RL demonstration upload skipped because an authenticated Steam Web API ticket was unavailable.");
            Destroy(gameObject);
            yield break;
        }

        ulong userId = ConfigData.GetUserId();
        if (userId == 0)
        {
            Debug.LogWarning("RL demonstration upload skipped because no authenticated user identity was available.");
            Destroy(gameObject);
            yield break;
        }
        _userId = userId.ToString(CultureInfo.InvariantCulture);
        _gameBuildVersion = BuildGameVersion();

        yield return ConnectToUploadServer();
        if (!_socketOpen)
        {
            Destroy(gameObject);
            yield break;
        }

        for (int i = 0; i < _candidateFiles.Count && !_haltUploads; i++)
        {
            yield return UploadFile(_candidateFiles[i]);
        }

        if (_socket != null && _socketOpen)
        {
            _socket.CloseAsync();
        }
        Destroy(gameObject);
    }

    private IEnumerator ConnectToUploadServer()
    {
        string url = $"wss://{ConfigData.ProductionServerHostname}:{ConfigData.ProductionPort}";
        _socketOpen = false;
        _socketClosed = false;
        _socket = new WebSocket(url, "game");
        _socket.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12;
        _socket.OnOpen += (_, __) => _socketOpen = true;
        _socket.OnClose += (_, __) =>
        {
            _socketOpen = false;
            _socketClosed = true;
        };
        _socket.OnError += (_, args) =>
        {
            if (args != null && !string.IsNullOrEmpty(args.Message))
            {
                _transportErrors.Enqueue(args.Message);
            }
        };
        _socket.OnMessage += (_, args) =>
        {
            if (args != null && args.IsText && !string.IsNullOrEmpty(args.Data))
            {
                _responses.Enqueue(args.Data);
            }
        };
        _socket.ConnectAsync();

        float deadline = Time.realtimeSinceStartup + ConnectionWaitSeconds;
        while (!_socketOpen && !_socketClosed && Time.realtimeSinceStartup < deadline)
        {
            DrainTransportErrors();
            yield return null;
        }
        DrainTransportErrors();
        if (!_socketOpen)
        {
            Debug.LogWarning($"RL demonstration upload could not connect to {url}; closed demonstrations remain local for a later run.");
        }
    }

    private IEnumerator UploadFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        FileInfo fileInfo = new FileInfo(filePath);
        if (!IsUploadBundleWithinLimit(fileInfo.Length, _manifestJson))
        {
            Debug.LogWarning($"Skipping RL demonstration outside the combined demo/manifest upload size limit: {filePath}");
            yield break;
        }

        string demoSha256;
        try
        {
            demoSha256 = ComputeFileSha256(filePath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Could not hash RL demonstration {filePath}: {exception.Message}");
            yield break;
        }

        BeginUploadRequest begin = new BeginUploadRequest
        {
            Type = "rl-demo-begin",
            Source = "Human",
            DemonstrationId = BuildDemonstrationId(demoSha256),
            GameBuildVersion = _gameBuildVersion,
            TotalBytes = fileInfo.Length,
            DemoSha256 = demoSha256,
            ManifestSha256 = _manifestSha256,
            ManifestJson = _manifestJson
        };
        yield return SendRequest(begin);
        if (!TryAcceptResponse(begin.Type, filePath, out UploadResponse response))
        {
            yield break;
        }
        if (response.Completed)
        {
            yield break;
        }
        if (string.IsNullOrEmpty(response.UploadId) || response.NextOffset < 0 ||
            response.NextOffset > fileInfo.Length)
        {
            Debug.LogWarning($"Server returned an invalid RL demonstration begin response for {filePath}.");
            yield break;
        }

        int chunkBytes = response.ChunkBytes > 0
            ? Math.Min(DefaultChunkBytes, response.ChunkBytes)
            : DefaultChunkBytes;
        long offset = response.NextOffset;
        byte[] buffer = new byte[chunkBytes];
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = offset;
            while (offset < fileInfo.Length && !_haltUploads)
            {
                int requested = (int)Math.Min(buffer.Length, fileInfo.Length - offset);
                int read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    Debug.LogWarning($"RL demonstration changed or became unreadable during upload: {filePath}");
                    yield break;
                }

                string data = Convert.ToBase64String(buffer, 0, read);
                ChunkUploadRequest chunk = new ChunkUploadRequest
                {
                    Type = "rl-demo-chunk",
                    UploadId = response.UploadId,
                    Offset = offset,
                    Data = data
                };
                yield return SendRequest(chunk);
                if (!TryAcceptResponse(chunk.Type, filePath, out response))
                {
                    yield break;
                }
                if (response.NextOffset <= offset || response.NextOffset > fileInfo.Length)
                {
                    Debug.LogWarning($"Server returned an invalid RL demonstration chunk offset for {filePath}.");
                    yield break;
                }
                offset = response.NextOffset;
                stream.Position = offset;
            }
        }

        if (_haltUploads || offset != fileInfo.Length)
        {
            yield break;
        }

        CompleteUploadRequest complete = new CompleteUploadRequest
        {
            Type = "rl-demo-complete",
            UploadId = response.UploadId
        };
        yield return SendRequest(complete);
        if (TryAcceptResponse(complete.Type, filePath, out response) && !response.Completed)
        {
            Debug.LogWarning($"Server did not mark RL demonstration upload complete for {filePath}.");
        }
    }

    private IEnumerator SendRequest(UploadRequest request)
    {
        _lastResponse = null;
        for (int attempt = 0; attempt < MaxRequestAttempts; attempt++)
        {
            if (_socket == null || !_socketOpen)
            {
                _haltUploads = true;
                yield break;
            }

            request.Hash = Utilities.Hash();
            request.UserId = _userId;
            request.AuthTicket = SteamWebApiAuth.TicketHex;
            long expectedHash = request.Hash;
            string rejectedTicket = request.AuthTicket;
            string json = JsonConvert.SerializeObject(request);
            int sendState = 0;
            try
            {
                _socket.SendAsync(json, succeeded =>
                    Interlocked.Exchange(ref sendState, succeeded ? 1 : -1));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"RL demonstration upload send failed: {exception.Message}");
                _haltUploads = true;
                yield break;
            }

            float sendDeadline = Time.realtimeSinceStartup + ResponseWaitSeconds;
            while (Volatile.Read(ref sendState) == 0 && _socketOpen &&
                   Time.realtimeSinceStartup < sendDeadline)
            {
                DrainTransportErrors();
                yield return null;
            }
            if (Volatile.Read(ref sendState) != 1)
            {
                if (!_socketOpen)
                {
                    _haltUploads = true;
                    yield break;
                }
                continue;
            }

            float responseDeadline = Time.realtimeSinceStartup + ResponseWaitSeconds;
            while (_socketOpen && Time.realtimeSinceStartup < responseDeadline)
            {
                DrainTransportErrors();
                if (TryDequeueMatchingResponse(request.Type, expectedHash, out UploadResponse response))
                {
                    if (response.Status == 401)
                    {
                        SteamWebApiAuth.Refresh();
                        yield return WaitForReplacementTicket(rejectedTicket);
                        break;
                    }
                    _lastResponse = response;
                    yield break;
                }
                yield return null;
            }
            if (!_socketOpen)
            {
                _haltUploads = true;
                yield break;
            }
        }

        Debug.LogWarning($"RL demonstration upload request {request.Type} did not receive a usable response; remaining demonstrations will stay local.");
        _haltUploads = true;
    }

    private IEnumerator WaitForReplacementTicket(string rejectedTicket)
    {
        float deadline = Time.realtimeSinceStartup + AuthenticationWaitSeconds;
        while (!SteamWebApiAuth.IsUnavailable && Time.realtimeSinceStartup < deadline)
        {
            if (SteamWebApiAuth.IsReady &&
                !string.Equals(SteamWebApiAuth.TicketHex, rejectedTicket, StringComparison.Ordinal))
            {
                yield break;
            }
            yield return null;
        }
    }

    private bool TryAcceptResponse(string requestType, string filePath, out UploadResponse response)
    {
        response = _lastResponse;
        _lastResponse = null;
        if (response == null)
        {
            return false;
        }
        if (response.Status >= 200 && response.Status < 300)
        {
            return true;
        }

        Debug.LogWarning(
            $"RL demonstration server rejected {requestType} for {Path.GetFileName(filePath)} " +
            $"with status {response.Status} ({response.ErrorCode ?? "unknown"}).");
        if (response.Status == 429 || response.Status == 503)
        {
            _haltUploads = true;
        }
        return false;
    }

    private bool TryDequeueMatchingResponse(string expectedType, long expectedHash, out UploadResponse response)
    {
        response = null;
        while (_responses.TryDequeue(out string json))
        {
            UploadResponse candidate;
            try
            {
                candidate = JsonConvert.DeserializeObject<UploadResponse>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Ignoring malformed RL demonstration upload response: {exception.Message}");
                continue;
            }
            if (candidate != null && candidate.Hash == expectedHash &&
                string.Equals(candidate.Type, expectedType, StringComparison.Ordinal))
            {
                response = candidate;
                return true;
            }
        }
        return false;
    }

    private void DrainTransportErrors()
    {
        while (_transportErrors.TryDequeue(out string message))
        {
            Debug.LogWarning($"RL demonstration upload WebSocket: {message}");
        }
    }

    private static string BuildGameVersion()
    {
        string version = string.IsNullOrEmpty(Application.version) ? "unknown" : Application.version;
        return string.IsNullOrEmpty(Application.buildGUID)
            ? version
            : $"{version}+{Application.buildGUID}";
    }

    private static string ComputeFileSha256(string filePath)
    {
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha = SHA256.Create())
        {
            return ToHex(sha.ComputeHash(stream));
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            return ToHex(sha.ComputeHash(bytes));
        }
    }

    private static string ToHex(byte[] bytes)
    {
        StringBuilder builder = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
