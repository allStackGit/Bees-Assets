#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

internal static class BeesCommandLineBuild
{
    private const string OutputArgument = "-beesOutput";
    private const string RlScene = "Assets/Scenes/RL 1v1 Training.unity";
    private static readonly string[] RlOversizedTexturePaths =
    {
        "Assets/Sprites/Ships/Parts/Ship Explosions/queen_death_body.png",
        "Assets/Sprites/Ships/Parts/Ship Explosions/queen_death_crown.png",
        "Assets/Sprites/Ships/Parts/Ship Explosions/queen_death_rings.png",
        "Assets/Sprites/Ships/Parts/Ship Explosions/queen_death_wings_eyes.png",
    };

    private static readonly string[] FullGameExcludedScenes =
    {
        RlScene,
        "Assets/Scenes/Hivemind Training.unity",
        "Assets/Scenes/Hivemind Training (4k).unity",
        "Assets/Scenes/Hivemind Training Downsized.unity",
        "Assets/Scenes/Sprite Mask Test.unity",
    };

    public static void BuildWindowsRl()
    {
        BuildRl(
            BuildTarget.StandaloneWindows64,
            "Bees RL Training.exe",
            StandaloneBuildSubtarget.Player);
    }

    public static void BuildLinuxRl()
    {
        BuildRl(
            BuildTarget.StandaloneLinux64,
            "Bees RL Training.x86_64",
            StandaloneBuildSubtarget.Server);
    }

    private static void BuildRl(
        BuildTarget target,
        string executableName,
        StandaloneBuildSubtarget subtarget)
    {
        TextureImporterPlatformSettings[] originalDefaultSettings =
            new TextureImporterPlatformSettings[RlOversizedTexturePaths.Length];
        try
        {
            for (int i = 0; i < RlOversizedTexturePaths.Length; i++)
            {
                TextureImporter importer =
                    AssetImporter.GetAtPath(RlOversizedTexturePaths[i]) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException(
                        $"Missing RL texture importer: {RlOversizedTexturePaths[i]}");
                }

                TextureImporterPlatformSettings settings =
                    importer.GetDefaultPlatformTextureSettings();
                originalDefaultSettings[i] = settings;

                if (settings.maxTextureSize > 8192)
                {
                    TextureImporterPlatformSettings rlSettings =
                        importer.GetDefaultPlatformTextureSettings();
                    rlSettings.maxTextureSize = 8192;
                    importer.SetPlatformTextureSettings(rlSettings);
                    importer.SaveAndReimport();
                }
            }

            Build(
                target,
                new[] { RlScene },
                executableName,
                subtarget);
        }
        finally
        {
            for (int i = 0; i < RlOversizedTexturePaths.Length; i++)
            {
                TextureImporter importer =
                    AssetImporter.GetAtPath(RlOversizedTexturePaths[i]) as TextureImporter;
                TextureImporterPlatformSettings original = originalDefaultSettings[i];
                if (importer != null && original != null)
                {
                    TextureImporterPlatformSettings current =
                        importer.GetDefaultPlatformTextureSettings();
                    if (current.maxTextureSize != original.maxTextureSize)
                    {
                        importer.SetPlatformTextureSettings(original);
                        importer.SaveAndReimport();
                    }
                }
            }
        }
    }

    public static void BuildWindowsFullGame()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
            .Select(scene => scene.path)
            .Where(path => !FullGameExcludedScenes.Any(
                excluded => string.Equals(path, excluded, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new InvalidOperationException(
                "The full-game build has no enabled EditorBuildSettings scenes.");
        }

        Build(BuildTarget.StandaloneWindows64, scenes, "Bees.exe");
    }

    private static void Build(
        BuildTarget target,
        string[] scenes,
        string executableName,
        StandaloneBuildSubtarget subtarget = StandaloneBuildSubtarget.Player)
    {
        string outputDirectory = ReadRequiredArgument(OutputArgument);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string locationPath = Path.Combine(outputDirectory, executableName);
        Debug.Log(
            $"[Bees build] target={target} subtarget={subtarget} " +
            $"scenes={scenes.Length} output={locationPath}");

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = locationPath,
            target = target,
            subtarget = (int)subtarget,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log(
            $"[Bees build] result={summary.result} duration={summary.totalTime} " +
            $"size={summary.totalSize} warnings={summary.totalWarnings} errors={summary.totalErrors}");

        if (summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Unity {target} build failed: {summary.result}; errors={summary.totalErrors}.");
        }

    }

    private static string ReadRequiredArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                string value = args[i + 1];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        throw new ArgumentException($"Missing required command-line argument {name}.");
    }
}
#endif
