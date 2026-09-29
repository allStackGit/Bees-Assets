using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

internal static class RlLivePolicyHotBundleBuilder
{
    private const string OutputArgument = "--bees-rl-hot-bundle-output=";
    private const string ModelAssetPath = "Assets/Resources/RlPolicy/BeesRL1v1.onnx";
    private const string ManifestAssetPath = "Assets/Resources/RlPolicy/BeesRL1v1Deployment.json";
    private const string BundleFileName = "bees-rl-policy.bundle";
    private const string MetadataFileName = "bees-rl-policy.metadata.json";
    private const string ModelAddress = "BeesRL1v1";
    private const string ManifestAddress = "BeesRL1v1Deployment";
    private const int MetadataSchemaVersion = 1;

    [Serializable]
    private sealed class DeploymentCompatibility
    {
        public string behavior_name;
        public int policy_abi_version;
        public int observation_schema_version;
        public int action_schema_version;
        public int reward_schema_version;
        public int scenario_schema_version;
    }

    [Serializable]
    private sealed class DeploymentIdentity
    {
        public int schema_version;
        public string model_id;
        public string model_sha256;
        public long model_size_bytes;
        public string behavior_name;
        public string policy_signature;
        public DeploymentCompatibility compatibility;
        public string game_build_version;
        public string training_run_id;
        public long training_step;
    }

    [Serializable]
    private sealed class DeploymentManifest
    {
        public int schema_version;
        public string deployment_id;
        public string identity_sha256;
        public DeploymentIdentity identity;
        public string model_file;
    }

    [Serializable]
    private sealed class BundleMetadata
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

    public static void BuildFromCommandLine()
    {
        string outputRoot = ReadRequiredOutputDirectory(Environment.GetCommandLineArgs());
        Directory.CreateDirectory(outputRoot);

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(ModelAssetPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);

        ModelAsset model = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelAssetPath);
        if (model == null)
        {
            throw new InvalidOperationException(
                $"Imported RL model is not a Unity Inference Engine ModelAsset: {ModelAssetPath}");
        }

        TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestAssetPath);
        if (manifestAsset == null)
        {
            throw new InvalidOperationException(
                $"RL deployment manifest could not be imported as a TextAsset: {ManifestAssetPath}");
        }

        DeploymentManifest manifest = JsonUtility.FromJson<DeploymentManifest>(manifestAsset.text);
        ValidateManifest(manifest);

        string modelFile = AssetPathToPhysicalPath(ModelAssetPath);
        string manifestFile = AssetPathToPhysicalPath(ManifestAssetPath);
        string actualModelSha256 = Sha256File(modelFile);
        if (!string.Equals(
                actualModelSha256,
                manifest.identity.model_sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Staged RL model bytes do not match the deployment manifest model hash.");
        }

        long actualModelSize = new FileInfo(modelFile).Length;
        if (actualModelSize != manifest.identity.model_size_bytes)
        {
            throw new InvalidOperationException(
                "Staged RL model byte size does not match the deployment manifest.");
        }

        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        string platform = PlatformName(target);

        AssetBundleBuild bundleBuild = new AssetBundleBuild
        {
            assetBundleName = BundleFileName,
            assetNames = new[]
            {
                ModelAssetPath,
                ManifestAssetPath,
            },
            addressableNames = new[]
            {
                ModelAddress,
                ManifestAddress,
            },
        };

        AssetBundleManifest bundleManifest = BuildPipeline.BuildAssetBundles(
            outputRoot,
            new[] { bundleBuild },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            target);
        if (bundleManifest == null)
        {
            throw new InvalidOperationException("Unity failed to build the RL champion AssetBundle.");
        }

        string bundlePath = Path.Combine(outputRoot, BundleFileName);
        if (!File.Exists(bundlePath))
        {
            throw new InvalidOperationException(
                $"Unity reported a successful AssetBundle build but the bundle is missing: {bundlePath}");
        }

        BundleMetadata metadata = new BundleMetadata
        {
            schema_version = MetadataSchemaVersion,
            platform = platform,
            build_target = target.ToString(),
            unity_version = Application.unityVersion,
            deployment_id = manifest.deployment_id,
            model_id = manifest.identity.model_id,
            model_sha256 = actualModelSha256,
            manifest_sha256 = Sha256File(manifestFile),
            policy_abi_version = manifest.identity.compatibility.policy_abi_version,
            policy_signature = manifest.identity.policy_signature,
            bundle_file = BundleFileName,
            bundle_sha256 = Sha256File(bundlePath),
            bundle_size_bytes = new FileInfo(bundlePath).Length,
            model_address = ModelAddress,
            manifest_address = ManifestAddress,
        };

        string metadataPath = Path.Combine(outputRoot, MetadataFileName);
        File.WriteAllText(
            metadataPath,
            JsonUtility.ToJson(metadata, true) + Environment.NewLine,
            new UTF8Encoding(false));

        Debug.Log(
            $"Built RL champion hot bundle deployment={metadata.deployment_id} " +
            $"model={metadata.model_id} platform={metadata.platform} " +
            $"bundle={bundlePath} metadata={metadataPath}");
    }

    private static string ReadRequiredOutputDirectory(string[] args)
    {
        foreach (string raw in args)
        {
            if (raw != null && raw.StartsWith(OutputArgument, StringComparison.Ordinal))
            {
                string value = raw.Substring(OutputArgument.Length).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return Path.GetFullPath(value);
                }
            }
        }

        throw new ArgumentException(
            $"Missing required command-line argument {OutputArgument}<directory>.");
    }

    private static void ValidateManifest(DeploymentManifest manifest)
    {
        if (manifest == null ||
            manifest.schema_version != 1 ||
            string.IsNullOrWhiteSpace(manifest.deployment_id) ||
            manifest.identity == null ||
            manifest.identity.schema_version != 1 ||
            string.IsNullOrWhiteSpace(manifest.identity.model_id) ||
            string.IsNullOrWhiteSpace(manifest.identity.model_sha256) ||
            manifest.identity.model_size_bytes <= 0 ||
            string.IsNullOrWhiteSpace(manifest.identity.policy_signature) ||
            manifest.identity.compatibility == null ||
            manifest.identity.compatibility.policy_abi_version <= 0 ||
            !string.Equals(manifest.model_file, "model.onnx", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged RL deployment manifest is missing required release identity fields.");
        }
    }

    private static string PlatformName(BuildTarget target)
    {
        switch (target)
        {
            case BuildTarget.StandaloneWindows64:
                return "WindowsPlayer";
            case BuildTarget.StandaloneLinux64:
                return "LinuxPlayer";
            case BuildTarget.StandaloneOSX:
                return "OSXPlayer";
            default:
                throw new InvalidOperationException(
                    $"Unsupported RL hot-bundle build target: {target}");
        }
    }

    private static string AssetPathToPhysicalPath(string assetPath)
    {
        const string prefix = "Assets/";
        if (!assetPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Expected an Assets-relative path: {assetPath}");
        }

        string relative = assetPath.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Application.dataPath, relative);
    }

    private static string Sha256File(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] digest = sha.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
