#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the already-verified staged live RL champion into a platform-specific AssetBundle.
/// The Python publisher independently validates the generated sidecar against the continual
/// registry before these bytes can become server-visible. This editor step is never authority.
/// </summary>
public static class RlLivePolicyHotBundleBuilder
{
    internal const int MetadataSchemaVersion = 1;
    internal const string BundleName = "bees-rl-policy";
    internal const string MetadataFileName = "bees-rl-policy.metadata.json";
    internal const string ModelAddress = "BeesRL1v1";
    internal const string ManifestAddress = "BeesRL1v1Deployment";
    internal const string ModelAssetPath = "Assets/Resources/RlPolicy/BeesRL1v1.onnx";
    internal const string ManifestAssetPath = "Assets/Resources/RlPolicy/BeesRL1v1Deployment.json";
    internal const string OutputArgument = "--bees-rl-hot-bundle-output=";

    [Serializable]
    private sealed class DeploymentCompatibility
    {
        public string behavior_name;
        public int policy_abi_version;
    }

    [Serializable]
    private sealed class DeploymentIdentity
    {
        public string model_id;
        public string model_sha256;
        public long model_size_bytes;
        public string behavior_name;
        public string policy_signature;
        public DeploymentCompatibility compatibility;
    }

    [Serializable]
    private sealed class DeploymentManifest
    {
        public int schema_version;
        public string deployment_id;
        public DeploymentIdentity identity;
        public string model_file;
    }

    [Serializable]
    private sealed class HotBundleMetadata
    {
        public int schema_version;
        public string platform;
        public string build_target;
        public string unity_version;
        public string deployment_id;
        public string model_id;
        public string model_sha256;
        public string manifest_sha256;
        public int policy_abi_version;
        public string policy_signature;
        public string bundle_file;
        public string bundle_sha256;
        public long bundle_size_bytes;
        public string model_address;
        public string manifest_address;
    }

    [MenuItem("Bees/RL/Build Current Champion Hot Bundle")]
    public static void BuildFromMenu()
    {
        string output = EditorUtility.OpenFolderPanel(
            "Build RL champion hot bundle",
            Path.GetDirectoryName(Application.dataPath),
            string.Empty);
        if (string.IsNullOrEmpty(output))
        {
            return;
        }
        Build(output, EditorUserBuildSettings.activeBuildTarget);
    }

    public static void BuildFromCommandLine()
    {
        string output = null;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(OutputArgument, StringComparison.Ordinal))
            {
                output = args[i].Substring(OutputArgument.Length).Trim('"');
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"Unity hot-bundle build requires {OutputArgument}<directory>.");
        }
        Build(output, EditorUserBuildSettings.activeBuildTarget);
    }

    internal static string Build(string outputDirectory, BuildTarget target)
    {
        RlPolicySchema.ValidateOrThrow();
        string platform = RuntimePlatformFor(target);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Hot-bundle output directory is required.", nameof(outputDirectory));
        }

        ModelAsset model = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelAssetPath);
        TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
        if (model == null || manifestAsset == null)
        {
            throw new InvalidOperationException(
                "Stage the current deployment with Training/bees_continual_unity_bundle.py before building a hot bundle.");
        }

        DeploymentManifest manifest = ParseAndValidateManifest(manifestAsset.text);
        string modelSourcePath = AbsoluteAssetPath(ModelAssetPath);
        string manifestSourcePath = AbsoluteAssetPath(ManifestAssetPath);
        if (!File.Exists(modelSourcePath) || !File.Exists(manifestSourcePath))
        {
            throw new FileNotFoundException("Staged deployment source files are missing from Resources/RlPolicy.");
        }
        FileInfo modelInfo = new FileInfo(modelSourcePath);
        string actualModelHash = ComputeFileSha256(modelSourcePath);
        if (modelInfo.Length != manifest.identity.model_size_bytes ||
            !string.Equals(actualModelHash, manifest.identity.model_sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Staged champion ONNX bytes do not match the deployment manifest.");
        }

        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        AssetBundleBuild build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { ModelAssetPath, ManifestAssetPath },
            addressableNames = new[] { ModelAddress, ManifestAddress },
        };
        AssetBundleManifest buildManifest = BuildPipeline.BuildAssetBundles(
            output,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            target);
        if (buildManifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the RL champion AssetBundle.");
        }

        string bundlePath = Path.Combine(output, BundleName);
        if (!File.Exists(bundlePath))
        {
            throw new FileNotFoundException("Unity reported success but the RL champion AssetBundle is missing.", bundlePath);
        }
        FileInfo bundleInfo = new FileInfo(bundlePath);
        if (bundleInfo.Length <= 0)
        {
            throw new InvalidDataException("Unity produced an empty RL champion AssetBundle.");
        }

        HotBundleMetadata metadata = new HotBundleMetadata
        {
            schema_version = MetadataSchemaVersion,
            platform = platform,
            build_target = target.ToString(),
            unity_version = Application.unityVersion,
            deployment_id = manifest.deployment_id,
            model_id = manifest.identity.model_id,
            model_sha256 = manifest.identity.model_sha256,
            manifest_sha256 = ComputeFileSha256(manifestSourcePath),
            policy_abi_version = RlPolicySchema.Version,
            policy_signature = RlPolicySchema.Signature,
            bundle_file = BundleName,
            bundle_sha256 = ComputeFileSha256(bundlePath),
            bundle_size_bytes = bundleInfo.Length,
            model_address = ModelAddress,
            manifest_address = ManifestAddress,
        };
        string metadataPath = Path.Combine(output, MetadataFileName);
        File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata, true) + "\n", new UTF8Encoding(false));
        Debug.Log(
            $"Built RL champion hot bundle deployment={metadata.deployment_id} platform={platform} " +
            $"bytes={metadata.bundle_size_bytes.ToString(CultureInfo.InvariantCulture)} path={bundlePath}");
        return metadataPath;
    }

    private static DeploymentManifest ParseAndValidateManifest(string json)
    {
        DeploymentManifest manifest;
        try
        {
            manifest = JsonUtility.FromJson<DeploymentManifest>(json);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Staged deployment manifest is invalid JSON.", exception);
        }
        if (manifest == null || manifest.schema_version != 1 || manifest.identity == null ||
            !IsContentId(manifest.deployment_id, "deploy-", 24) ||
            !IsContentId(manifest.identity.model_id, $"bees-rl-v{RlPolicySchema.Version}-", 24) ||
            !IsLowerHex(manifest.identity.model_sha256, 64) || manifest.identity.model_size_bytes <= 0 ||
            !string.Equals(manifest.model_file, "model.onnx", StringComparison.Ordinal) ||
            !string.Equals(manifest.identity.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal) ||
            !string.Equals(manifest.identity.policy_signature, RlPolicySchema.Signature, StringComparison.Ordinal) ||
            manifest.identity.compatibility == null ||
            manifest.identity.compatibility.policy_abi_version != RlPolicySchema.Version ||
            !string.Equals(manifest.identity.compatibility.behavior_name, RlPolicySchema.ExpectedBehaviorName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Staged deployment manifest is incompatible with the compiled RL policy.");
        }
        return manifest;
    }

    private static string RuntimePlatformFor(BuildTarget target)
    {
        switch (target)
        {
            case BuildTarget.StandaloneWindows64:
                return "WindowsPlayer";
            case BuildTarget.StandaloneOSX:
                return "OSXPlayer";
            case BuildTarget.StandaloneLinux64:
                return "LinuxPlayer";
            default:
                throw new NotSupportedException(
                    $"RL champion hot bundles currently support desktop player targets only, not {target}.");
        }
    }

    private static string AbsoluteAssetPath(string assetPath)
    {
        if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected an Assets-relative path.", nameof(assetPath));
        }
        return Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ComputeFileSha256(string path)
    {
        using (FileStream stream = File.OpenRead(path))
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
#endif
