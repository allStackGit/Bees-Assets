#if UNITY_EDITOR
using System;
using System.IO;
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
        string filename = "unity-stage-asteroids-" + safe + ".json";
        var manifest = new Palette {
            stage_name = stageName,
            asteroid_max_speed_exclusive = stage.AsteroidMaxSpeed,
            prefab_numbers = numbers
        };
        string output = Path.Combine(directory, filename);
        File.WriteAllText(
            output, JsonUtility.ToJson(manifest, true) + "\n",
            new UTF8Encoding(false));
        AssetDatabase.Refresh();
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
