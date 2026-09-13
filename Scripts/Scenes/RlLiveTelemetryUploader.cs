using Assets.Scripts;
using Assets.Scripts.Server;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using WebSocketSharp;

/// <summary>
/// Automatically uploads completed live-RL player telemetry segments to BeesServer's authenticated
/// quarantine endpoint. Files are deleted locally only after the server confirms immutable archival
/// (or an identical duplicate). Network/auth/rate-limit failures leave the durable Pending backlog
/// intact for a later retry.
/// </summary>
internal sealed class RlLiveTelemetryUploader : MonoBehaviour
{
    internal const int MaxPayloadBytes = 16 * 1024 * 1024;
    internal const int DefaultChunkBytes = 512 * 1024;

    private const float AuthenticationWaitSeconds = 60f;
    private const float ConnectionWaitSeconds = 15f;
    private const float ResponseWaitSeconds = 15f;
    private const float RescanSeconds = 10f;
    private const int MaxRequestAttempts = 3;

    private abstract class UploadRequest
    {
        public string Type;
        public long Hash;
        public string UserId;
        public string AuthTicket;
    }

    private sealed class BeginUploadRequest : UploadRequest
    {
        public string MatchId;
        public string GameBuildVersion;
        public long TotalBytes;
        public string PayloadSha256;
        public string ModelId;
        public string ModelSha256;
        public string DeploymentId;
        public int PolicyAbiVersion;
        public string PolicySignature;
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
        public long TotalBytes;
    }

    private static bool _installed;
    private readonly ConcurrentQueue<string> _responses = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _transportErrors = new ConcurrentQueue<string>();

    private WebSocket _socket;
    private volatile bool _socketOpen;
    private volatile bool _socketClosed;
    private string _userId;
    private UploadResponse _lastResponse;
    private bool _haltUploads;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        if (_installed || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime ||
            IsDisabled(Environment.GetCommandLineArgs()))
        {
            return;
        }
        _installed = true;
        GameObject host = new GameObject("RL Live Telemetry Uploader");
        DontDestroyOnLoad(host);
        host.AddComponent<RlLiveTelemetryUploader>();
    }

    private void Awake()
    {
#if UNITY_WEBGL
        Destroy(gameObject);
#else
        if (!ConfigData.Production)
        {
            Destroy(gameObject);
            return;
        }
        try
        {
            RlPolicySchema.ValidateOrThrow();
            Directory.CreateDirectory(RlLiveTelemetryRecorder.GetPendingDirectory());
        }
        catch (Exception exception)
        {
            Debug.LogError("Automatic RL telemetry uploader disabled: " + exception.Message);
            Destroy(gameObject);
        }
#endif
    }

    private IEnumerator Start()
    {
#if !UNITY_WEBGL
        while (!_haltUploads)
        {
            yield return UploadPendingFiles();
            if (!_haltUploads)
            {
                yield return new WaitForSecondsRealtime(RescanSeconds);
            }
        }
#endif
    }

    private void OnDestroy()
    {
#if !UNITY_WEBGL
        CloseSocket();
#endif
    }

    private static bool IsDisabled(System.Collections.Generic.IReadOnlyList<string> args)
    {
        if (args == null)
        {
            return false;
        }
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(
                    args[i],
                    RlLiveTelemetryRecorder.DisableCommandLineFlag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerator UploadPendingFiles()
    {
        string pending = RlLiveTelemetryRecorder.GetPendingDirectory();
        string[] files;
        try
        {
            files = Directory.GetFiles(pending, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not enumerate pending RL telemetry: " + exception.Message);
            yield break;
        }

        for (int i = 0; i < files.Length && !_haltUploads; i++)
        {
            string path = files[i];
            byte[] bytes;
            RlLiveTelemetryRecorder.TelemetryPayload payload;
            string sha256;
            try
            {
                bytes = File.ReadAllBytes(path);
                if (bytes.Length <= 0 || bytes.Length > MaxPayloadBytes)
                {
                    QuarantineLocalInvalid(path, "payload-size");
                    continue;
                }
                payload = JsonConvert.DeserializeObject<RlLiveTelemetryRecorder.TelemetryPayload>(
                    Encoding.UTF8.GetString(bytes));
                if (!PayloadMatchesCurrentContract(payload))
                {
                    QuarantineLocalInvalid(path, "payload-contract");
                    continue;
                }
                sha256 = ComputeSha256(bytes);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not read pending RL telemetry " + path + ": " + exception.Message);
                QuarantineLocalInvalid(path, "payload-read");
                continue;
            }

            bool completed = false;
            yield return UploadOne(bytes, payload, sha256, value => completed = value);
            if (completed)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Server archived RL telemetry but local pending file could not be removed: " + exception.Message);
                }
            }
            else
            {
                // Preserve ordering and avoid repeatedly consuming server quota after the first failure.
                yield break;
            }
        }
    }

    private IEnumerator UploadOne(
        byte[] payloadBytes,
        RlLiveTelemetryRecorder.TelemetryPayload payload,
        string payloadSha256,
        Action<bool> setCompleted)
    {
        setCompleted(false);
        yield return EnsureAuthenticated();
        if (_haltUploads || string.IsNullOrEmpty(_userId))
        {
            yield break;
        }
        yield return Connect();
        if (!_socketOpen)
        {
            yield break;
        }

        BeginUploadRequest begin = CreateRequest<BeginUploadRequest>("rl-telemetry-begin");
        begin.MatchId = payload.match_id;
        begin.GameBuildVersion = payload.game_build_version;
        begin.TotalBytes = payloadBytes.LongLength;
        begin.PayloadSha256 = payloadSha256;
        begin.ModelId = payload.model_id;
        begin.ModelSha256 = payload.model_sha256;
        begin.DeploymentId = payload.deployment_id;
        begin.PolicyAbiVersion = payload.policy_abi_version;
        begin.PolicySignature = payload.policy_signature;
        yield return SendRequest(begin);
        if (!TryAcceptResponse("rl-telemetry-begin", out UploadResponse response))
        {
            CloseSocket();
            yield break;
        }
        if (response.Completed)
        {
            setCompleted(true);
            CloseSocket();
            yield break;
        }
        if (string.IsNullOrEmpty(response.UploadId))
        {
            CloseSocket();
            yield break;
        }

        string uploadId = response.UploadId;
        long offset = response.NextOffset;
        int chunkBytes = response.ChunkBytes > 0
            ? Math.Min(response.ChunkBytes, DefaultChunkBytes)
            : DefaultChunkBytes;
        while (offset < payloadBytes.LongLength)
        {
            int length = (int)Math.Min(chunkBytes, payloadBytes.LongLength - offset);
            byte[] chunk = new byte[length];
            Buffer.BlockCopy(payloadBytes, (int)offset, chunk, 0, length);
            ChunkUploadRequest request = CreateRequest<ChunkUploadRequest>("rl-telemetry-chunk");
            request.UploadId = uploadId;
            request.Offset = offset;
            request.Data = Convert.ToBase64String(chunk);
            yield return SendRequest(request);
            if (!TryAcceptResponse("rl-telemetry-chunk", out response) ||
                response.NextOffset <= offset || response.NextOffset > payloadBytes.LongLength)
            {
                CloseSocket();
                yield break;
            }
            offset = response.NextOffset;
        }

        CompleteUploadRequest complete = CreateRequest<CompleteUploadRequest>("rl-telemetry-complete");
        complete.UploadId = uploadId;
        yield return SendRequest(complete);
        if (TryAcceptResponse("rl-telemetry-complete", out response) && response.Completed)
        {
            setCompleted(true);
        }
        CloseSocket();
    }

    private static bool PayloadMatchesCurrentContract(RlLiveTelemetryRecorder.TelemetryPayload payload)
    {
        return payload != null && payload.schema_version == 1 &&
            !string.IsNullOrEmpty(payload.match_id) &&
            !string.IsNullOrEmpty(payload.game_build_version) &&
            !string.IsNullOrEmpty(payload.model_id) &&
            !string.IsNullOrEmpty(payload.model_sha256) && payload.model_sha256.Length == 64 &&
            !string.IsNullOrEmpty(payload.deployment_id) &&
            string.Equals(payload.policy_signature, RlPolicySchema.Signature, StringComparison.Ordinal) &&
            string.Equals(payload.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) &&
            payload.policy_abi_version == RlPolicySchema.Version &&
            payload.observation_schema_version == RlPolicySchema.ObservationSchemaVersion &&
            payload.action_schema_version == RlPolicySchema.ActionSchemaVersion &&
            payload.reward_schema_version == RlPolicySchema.RewardSchemaVersion &&
            payload.scenario_schema_version == RlPolicySchema.ScenarioSchemaVersion &&
            payload.steps != null && payload.steps.Count > 0;
    }

    private IEnumerator EnsureAuthenticated()
    {
        _userId = null;
        SteamWebApiAuth.EnsureRequested();
        float deadline = Time.realtimeSinceStartup + AuthenticationWaitSeconds;
        while (!SteamWebApiAuth.IsReady && !SteamWebApiAuth.IsUnavailable &&
               Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }
        if (!SteamWebApiAuth.IsReady)
        {
            yield break;
        }
        ulong userId = ConfigData.GetUserId();
        if (userId != 0)
        {
            _userId = userId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private IEnumerator Connect()
    {
        CloseSocket();
        while (_responses.TryDequeue(out _)) { }
        while (_transportErrors.TryDequeue(out _)) { }
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
    }

    private T CreateRequest<T>(string type) where T : UploadRequest, new()
    {
        return new T
        {
            Type = type,
            UserId = _userId,
            AuthTicket = SteamWebApiAuth.TicketHex
        };
    }

    private IEnumerator SendRequest(UploadRequest request)
    {
        _lastResponse = null;
        for (int attempt = 0; attempt < MaxRequestAttempts; attempt++)
        {
            if (_socket == null || !_socketOpen)
            {
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
                Debug.LogWarning("RL telemetry send failed: " + exception.Message);
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
                yield break;
            }
        }
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

    private bool TryAcceptResponse(string requestType, out UploadResponse response)
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
        if (response.Status == 409 &&
            (string.Equals(response.ErrorCode, "incompatible-policy", StringComparison.Ordinal) ||
             string.Equals(response.ErrorCode, "payload-identity-mismatch", StringComparison.Ordinal)))
        {
            _haltUploads = true;
        }
        if (response.Status != 429)
        {
            Debug.LogWarning(
                $"RL telemetry server rejected {requestType} with status {response.Status} " +
                $"({response.ErrorCode ?? "unknown"}).");
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
                Debug.LogWarning("Ignoring malformed RL telemetry response: " + exception.Message);
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
            Debug.LogWarning("RL telemetry transport error: " + message);
        }
    }

    private void CloseSocket()
    {
        if (_socket != null)
        {
            try
            {
                _socket.CloseAsync();
            }
            catch
            {
                // Optional background transport is already being abandoned.
            }
            _socket = null;
        }
        _socketOpen = false;
        _socketClosed = false;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] digest = sha.ComputeHash(bytes);
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++)
            {
                builder.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    private static void QuarantineLocalInvalid(string path, string reason)
    {
        try
        {
            string root = Path.Combine(
                Application.persistentDataPath,
                RlLiveTelemetryRecorder.TelemetryDirectoryName,
                $"PolicyV{RlPolicySchema.Version}",
                "Invalid");
            Directory.CreateDirectory(root);
            string destination = Path.Combine(
                root,
                Path.GetFileNameWithoutExtension(path) + "." + reason + ".json");
            if (!File.Exists(destination))
            {
                File.Move(path, destination);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not quarantine invalid local RL telemetry: " + exception.Message);
        }
    }
}
