#if UNITY_EDITOR
using System;
using Assets.Scripts.Entities;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Export the SELECTED Stage's exact, ordered authored ordinary collision
/// asteroid prefab palette. The portable simulation must never infer this
/// list from the 28 available collider assets: Stage.Prefabs can be a subset,
/// may reorder variants and may contain intentional duplicate weights.
/// </summary>
internal static class NativeAsteroidPaletteExporter
{
    [Serializable]
    private sealed class Palette
    {
        public int schema_version = 1;
        public string stage_name;
        public int asteroid_max_speed_exclusive;
        public int[] prefab_numbers;
    }

    [MenuItem("Bees/Native/Export Selected Stage Asteroid Palette")]
    private static void ExportSelectedStage()
    {
        var stage = Selection.activeGameObject == null
            ? null : Selection.activeGameObject.GetComponent<Stage>();
        ExportStage(stage, contentAddressed: false);
        AssetDatabase.Refresh();
    }

    [MenuItem("Bees/Native/Export All Active Stage Asteroid Palettes")]
    private static void ExportAllActive()
    {
        var stages = UnityEngine.Object.FindObjectsByType<Stage>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        if (stages.Length == 0)
            throw new InvalidOperationException("There are no active Stages with source asteroid palettes.");
        Array.Sort(stages, (first, second) =>
            StringComparer.Ordinal.Compare(first.gameObject.name, second.gameObject.name));
        foreach (Stage stage in stages)
            ExportStage(stage, contentAddressed: true);
        AssetDatabase.Refresh();
        Debug.Log("Exported physical asteroid catalogs from " +
            stages.Length + " active original Stages.");
    }

    private static void ExportStage(Stage stage, bool contentAddressed)
    {
        if (stage == null || stage.Prefabs == null ||
            stage.Prefabs.CollisionAsteroidPrefabs == null ||
            stage.Prefabs.CollisionAsteroidPrefabs.Count == 0 ||
            stage.AsteroidMaxSpeed < 0)
        {
            throw new InvalidOperationException(
                "Select an authored Stage with a valid collision-asteroid prefab palette.");
        }

        var source = stage.Prefabs.CollisionAsteroidPrefabs;
        var numbers = new int[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            GameObject prefab = source[i];
            if (prefab == null || prefab.GetComponent<CollisionAsteroid>() == null ||
                !prefab.name.StartsWith("Asteroid ", StringComparison.Ordinal) ||
                !int.TryParse(prefab.name.Substring("Asteroid ".Length), out int number) ||
                number < 1 || number > 28)
            {
                throw new InvalidOperationException(
                    "Missing or unrecognized full CollisionAsteroid prefab at index " + i);
            }
            numbers[i] = number;
        }
        string stageName = stage.gameObject.name;
        if (string.IsNullOrWhiteSpace(stageName))
        {
            throw new InvalidOperationException("Stage requires an authored name.");
        }

        // Stage names are never trusted as filesystem paths.
        var safe = new StringBuilder(stageName.Length);
        foreach (char character in stageName)
        {
            safe.Append(char.IsLetterOrDigit(character) ||
                character == '-' || character == '_' ? character : '_');
        }
        string directory = Path.Combine(Application.dataPath, "native", "assets");
        Directory.CreateDirectory(directory);
        var manifest = new Palette {
            stage_name = stageName,
            asteroid_max_speed_exclusive = stage.AsteroidMaxSpeed,
            prefab_numbers = numbers
        };
        string text = JsonUtility.ToJson(manifest, true) + "\n";
        string filename = "unity-stage-asteroids-" + safe;
        if (contentAddressed)
        {
            // A later original mission may load a Stage with the same name
            // but another ordered prefab weighting. Never overwrite an
            // older physical simulation contract from that mission.
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            filename += "-" + BitConverter.ToString(digest, 0, 12)
                .Replace("-", "").ToLowerInvariant();
        }
        string output = Path.Combine(directory, filename + ".json");
        if (contentAddressed && File.Exists(output))
        {
            if (!string.Equals(File.ReadAllText(output, Encoding.UTF8), text,
                    StringComparison.Ordinal))
                throw new IOException("Original Stage palette hash collision: " + output);
        }
        else
        {
            File.WriteAllText(output, text, new UTF8Encoding(false));
        }
        Debug.Log("Exported Stage-specific native collision asteroids: " + output);
    }

    [MenuItem("Bees/Native/Export Selected Stage Asteroid Palette", true)]
    private static bool CanExportSelectedStage()
    {
        return Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponent<Stage>() != null;
    }
}
#endif
