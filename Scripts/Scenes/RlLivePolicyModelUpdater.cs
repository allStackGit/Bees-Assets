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
using Unity.InferenceEngine;
using UnityEngine;
using WebSocketSharp;

/// <summary>
/// Polls BeesServer for a newer compatible live RL champion and hot-swaps it only after the
/// complete platform AssetBundle has passed client-side size, SHA-256, manifest, model-id and
/// policy-ABI checks. Download or validation failures leave the already-running champion intact.
/// </summary>
internal sealed class RlLivePolicyModelUpdater : MonoBehaviour
{
    internal const string CurrentRequestType = "rl-model-current";
    internal const string ChunkRequestType = "rl-model-chunk";
    internal const string ModelAddress = "BeesRL1v1";
    internal const string ManifestAddress = "BeesRL1v1Deployment";
    internal const int DefaultChunkBytes = 512 * 1024;
    internal const long MaxBundleBytes = 512L * 1024L * 1024L;

    private const float AuthenticationWaitSeconds = 60f;
    private const float ConnectionWaitSeconds = 15f;
    private const float ResponseWaitSeconds = 15f;
    private const float PollIntervalSeconds = 300f;
    private const int MaxRequestAttempts = 3;

    private abstract class ModelRequest
    {
        public string Type;
        public long Hash;
        public string UserId;
        public string AuthTicket;
        public int PolicyAbiVersion;
        public string PolicySignature;
        public string Platform;
    }

    private sealed class CurrentModelRequest : ModelRequest
    {
        public string CurrentDeploymentId;
    }

    private sealed class ChunkModelRequest : ModelRequest
    {
        public string DeploymentId;
        public string BundleSha256;
        public long Offset;
        public int Length;
    }

    private sealed class ModelResponse
    {
        public string Type;
        public long Hash;
        public int Status;
        public string ErrorCode;
        public string Platform;
        public bool UpToDate;
        public string DeploymentId;
        public string ModelId;
        public string BundleSha256;
        public long BundleSizeBytes;
        public int ChunkBytes;
        public int PolicyAbiVersion;
        public string PolicySignature;
        public long Offset;
        public long NextOffset;
        public bool Complete;
        public string Data;
    }

    private readonly ConcurrentQueue<string> _responses = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<string> _transportErrors = new ConcurrentQueue<string>();

    private RlLivePolicyModelBootstrap _bootstrap;
    private WebSocket _socket;
    private volatile bool _socketOpen;
    private volatile bool _socketClosed;
    private string _userId;
    private string _platform;
    private string _currentDeploymentId;
    private ModelResponse _lastResponse;
    private bool _disabled;

    internal void Initialize(RlLivePolicyModelBootstrap bootstrap, string currentDeploymentId)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _currentDeploymentId = currentDeploymentId;
    }

    private IEnumerator Start()
    {
#if UNITY_WEBGL
        Destroy(this);
        yield break;
#else
        if (_bootstrap == null || !ConfigData.Production)
        {
            Destroy(this);
            yield break;
        }

        _platform = PlatformName(Application.platform);
        if (string.IsNullOrEmpty(_platform))
        {
            Debug.LogWarning($"Live RL champion hot distribution is unsupported on {Application.platform}.");
            Destroy(this);
            yield break;
        }

        while (!_disabled && _bootstrap != null)
        {
            yield return PollOnce();
            if (!_disabled && _bootstrap != null)
            {
                yield return new WaitForSecondsRealtime(PollIntervalSeconds);
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

    private IEnumerator PollOnce()
    {
        yield return EnsureAuthenticated();
        if (_disabled || string.IsNullOrEmpty(_userId))
        {
            yield break;
        }

        yield return Connect();
        if (!_socketOpen)
        {
            yield break;
        }

        CurrentModelRequest current = CreateRequest<CurrentModelRequest>(CurrentRequestType);
        current.CurrentDeploymentId = _currentDeploymentId;
        yield return SendRequest(current);
        if (!TryAcceptResponse(CurrentRequestType, out ModelResponse response))
        {
            CloseSocket();
            yield break;
        }

        if (!TryValidateCurrentResponse(response, _platform, _currentDeploymentId, out string validationError))
        {
            Debug.LogWarning("Rejected RL model distribution pointer: " + validationError);
            CloseSocket();
            yield break;
        }
        if (response.UpToDate)
        {
            CloseSocket();
            yield break;
        }

        yield return DownloadAndApply(response);
        CloseSocket();
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

    private IEnumerator DownloadAndApply(ModelResponse descriptor)
    {
        string cacheRoot = Path.Combine(Application.persistentDataPath, "RlPolicyHotBundles");
        string finalPath;
        string tempPath;
        try
        {
            Directory.CreateDirectory(cacheRoot);
            finalPath = Path.Combine(cacheRoot, descriptor.DeploymentId + ".bundle");
            tempPath = finalPath + ".partial";
            TryDelete(tempPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not prepare RL champion download cache: " + exception.Message);
            yield break;
        }

        int chunkBytes = Math.Min(
            DefaultChunkBytes,
            descriptor.ChunkBytes > 0 ? descriptor.ChunkBytes : DefaultChunkBytes);
        long offset = 0;
        FileStream output;
        try
        {
            output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not create RL champion temporary bundle: " + exception.Message);
            yield break;
        }

        using (output)
        {
            while (offset < descriptor.BundleSizeBytes)
            {
                ChunkModelRequest chunk = CreateRequest<ChunkModelRequest>(ChunkRequestType);
                chunk.DeploymentId = descriptor.DeploymentId;
                chunk.BundleSha256 = descriptor.BundleSha256;
                chunk.Offset = offset;
                chunk.Length = (int)Math.Min(chunkBytes, descriptor.BundleSizeBytes - offset);
                yield return SendRequest(chunk);

                if (!TryAcceptResponse(ChunkRequestType, out ModelResponse response))
                {
                    TryDelete(tempPath);
                    yield break;
                }
                if (!TryValidateChunkResponse(
                        response,
                        descriptor,
                        offset,
                        out byte[] bytes,
                        out string chunkError))
                {
                    Debug.LogWarning("Rejected RL model distribution chunk: " + chunkError);
                    TryDelete(tempPath);
                    yield break;
                }

                try
                {
                    output.Write(bytes, 0, bytes.Length);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Could not write RL champion download chunk: " + exception.Message);
                    TryDelete(tempPath);
                    yield break;
                }
                offset = response.NextOffset;
            }

            try
            {
                output.Flush(true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Could not flush RL champion temporary bundle: " + exception.Message);
                TryDelete(tempPath);
                yield break;
            }
        }

        string actualSha256;
        try
        {
            FileInfo completed = new FileInfo(tempPath);
            if (completed.Length != descriptor.BundleSizeBytes)
            {
                Debug.LogWarning("Downloaded RL champion bundle size does not match the authenticated server descriptor.");
                TryDelete(tempPath);
                yield break;
            }
            actualSha256 = ComputeFileSha256(tempPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not verify downloaded RL champion bundle: " + exception.Message);
            TryDelete(tempPath);
            yield break;
        }
        if (!string.Equals(actualSha256, descriptor.BundleSha256, StringComparison.Ordinal))
        {
            Debug.LogWarning("Downloaded RL champion bundle failed SHA-256 verification; keeping the current champion.");
            TryDelete(tempPath);
            yield break;
        }

        try
        {
            TryDelete(finalPath);
            File.Move(tempPath, finalPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Could not finalize verified RL champion bundle cache: " + exception.Message);
            TryDelete(tempPath);
            yield break;
        }

        AssetBundleCreateRequest loadBundle = AssetBundle.LoadFromFileAsync(finalPath);
        yield return loadBundle;
        AssetBundle bundle = loadBundle.assetBundle;
        if (bundle == null)
        {
            Debug.LogWarning("Verified RL champion bytes could not be loaded as a Unity AssetBundle; keeping the current champion.");
            yield break;
        }

        AssetBundleRequest modelRequest = bundle.LoadAssetAsync<ModelAsset>(ModelAddress);
        AssetBundleRequest manifestRequest = bundle.LoadAssetAsync<TextAsset>(ManifestAddress);
        yield return modelRequest;
        yield return manifestRequest;

        ModelAsset model = modelRequest.asset as ModelAsset;
        TextAsset manifest = manifestRequest.asset as TextAsset;
        if (model == null || manifest == null)
        {
            bundle.Unload(true);
            Debug.LogWarning("RL champion AssetBundle is missing the expected model or deployment manifest address.");
            yield break;
        }

        bool applied;
        string applyError;
        try
        {
            applied = _bootstrap.TryApplyHotBundle(
                model,
                manifest.text,
                descriptor.DeploymentId,
                descriptor.ModelId,
                out applyError);
        }
        catch (Exception exception)
        {
            applied = false;
            applyError = exception.GetType().Name + ": " + exception.Message;
        }

        bundle.Unload(!applied);
        if (!applied)
        {
            Debug.LogWarning("RL champion hot-swap rejected; keeping the current champion: " + applyError);
            yield break;
        }

        _currentDeploymentId = descriptor.DeploymentId;
        Debug.Log(
            $"Live RL champion hot-swapped: deployment={descriptor.DeploymentId} " +
            $"model={descriptor.ModelId} platform={descriptor.Platform}.");
    }

    private T CreateRequest<T>(string type) where T : ModelRequest, new()
    {
        return new T
        {
            Type = type,
            UserId = _userId,
            AuthTicket = SteamWebApiAuth.TicketHex,
            PolicyAbiVersion = RlPolicySchema.Version,
            PolicySignature = RlPolicySchema.Signature,
            Platform = _platform,
        };
    }

    private IEnumerator SendRequest(ModelRequest request)
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
                Debug.LogWarning("RL model distribution send failed: " + exception.Message);
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
                if (TryDequeueMatchingResponse(request.Type, expectedHash, out ModelResponse response))
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

    private bool TryAcceptResponse(string requestType, out ModelResponse response)
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
            string.Equals(response.ErrorCode, "incompatible-client-policy", StringComparison.Ordinal))
        {
            _disabled = true;
        }
        if (response.Status != 404 ||
            !string.Equals(response.ErrorCode, "model-not-published", StringComparison.Ordinal))
        {
            Debug.LogWarning(
                $"RL model distribution server rejected {requestType} with status {response.Status} " +
                $"({response.ErrorCode ?? "unknown"}).");
        }
        return false;
    }

    private bool TryDequeueMatchingResponse(string expectedType, long expectedHash, out ModelResponse response)
    {
        response = null;
        while (_responses.TryDequeue(out string json))
        {
            ModelResponse candidate;
            try
            {
                candidate = JsonConvert.DeserializeObject<ModelResponse>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Ignoring malformed RL model distribution response: " + exception.Message);
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
            Debug.LogWarning("RL model distribution WebSocket: " + message);
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
                // This is an optional background connection; closing is best-effort.
            }
            _socket = null;
        }
        _socketOpen = false;
        _socketClosed = true;
    }

    internal static string PlatformName(RuntimePlatform platform)
    {
        switch (platform)
        {
            case RuntimePlatform.WindowsPlayer:
                return "WindowsPlayer";
            case RuntimePlatform.OSXPlayer:
                return "OSXPlayer";
            case RuntimePlatform.LinuxPlayer:
                return "LinuxPlayer";
            default:
                return null;
        }
    }

    private static bool TryValidateCurrentResponse(
        ModelResponse response,
        string expectedPlatform,
        string currentDeploymentId,
        out string error)
    {
        error = null;
        if (response == null ||
            !string.Equals(response.Platform, expectedPlatform, StringComparison.Ordinal) ||
            response.PolicyAbiVersion != RlPolicySchema.Version ||
            !string.Equals(response.PolicySignature, RlPolicySchema.Signature, StringComparison.Ordinal))
        {
            error = "server policy/platform identity does not match this build";
            return false;
        }
        if (!IsContentId(response.DeploymentId, "deploy-", 24) ||
            !IsContentId(response.ModelId, $"bees-rl-v{RlPolicySchema.Version}-", 24) ||
            !IsLowerHex(response.BundleSha256, 64) ||
            response.BundleSizeBytes <= 0 || response.BundleSizeBytes > MaxBundleBytes ||
            response.ChunkBytes <= 0 || response.ChunkBytes > DefaultChunkBytes)
        {
            error = "server bundle descriptor is malformed or outside client bounds";
            return false;
        }
        bool actuallyUpToDate = string.Equals(
            currentDeploymentId,
            response.DeploymentId,
            StringComparison.Ordinal);
        if (response.UpToDate != actuallyUpToDate)
        {
            error = "server UpToDate flag disagrees with the deployment identity";
            return false;
        }
        return true;
    }

    private static bool TryValidateChunkResponse(
        ModelResponse response,
        ModelResponse descriptor,
        long expectedOffset,
        out byte[] bytes,
        out string error)
    {
        bytes = null;
        error = null;
        if (response == null || descriptor == null ||
            !string.Equals(response.Platform, descriptor.Platform, StringComparison.Ordinal) ||
            !string.Equals(response.DeploymentId, descriptor.DeploymentId, StringComparison.Ordinal) ||
            !string.Equals(response.BundleSha256, descriptor.BundleSha256, StringComparison.Ordinal) ||
            response.Offset != expectedOffset || response.NextOffset <= expectedOffset ||
            response.NextOffset > descriptor.BundleSizeBytes || string.IsNullOrEmpty(response.Data))
        {
            error = "chunk identity or byte range does not match the pinned deployment";
            return false;
        }
        try
        {
            bytes = Convert.FromBase64String(response.Data);
        }
        catch (FormatException)
        {
            error = "chunk payload is not valid base64";
            return false;
        }
        if (bytes.LongLength != response.NextOffset - response.Offset ||
            response.Complete != (response.NextOffset == descriptor.BundleSizeBytes))
        {
            bytes = null;
            error = "chunk payload length/completion marker is inconsistent";
            return false;
        }
        return true;
    }

    private static string ComputeFileSha256(string filePath)
    {
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Could not clean up RL model bundle cache file {path}: {exception.Message}");
        }
    }

    private static bool IsContentId(string value, string prefix, int hexLength)
    {
        return value != null && value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.Length == prefix.Length + hexLength &&
               IsLowerHex(value.Substring(prefix.Length), hexLength);
    }

    private static bool IsLowerHex(string value, int expectedLength)
    {
        if (value == null || value.Length != expectedLength)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
}
