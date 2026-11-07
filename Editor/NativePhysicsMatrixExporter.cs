#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Extract the current authoritative Physics2D collision-layer configuration
/// for the cross-platform native game and headless worker. No hardcoded
/// inferred layer masks or permissive defaults may enter training.
/// </summary>
internal static class NativePhysicsMatrixExporter
{
    [MenuItem("Bees/Native/Export Physics2D Collision Matrix")]
    private static void Export()
    {
        // Unity's Assets directory is this repository's root. Keep the
        // artifact inside native/assets so both Linux and Windows use the
        // same committed physics contract after a Unity Editor export.
        string targetDirectory = Path.Combine(Application.dataPath, "native", "assets");
        Directory.CreateDirectory(targetDirectory);
        string output = Path.Combine(targetDirectory, "unity-physics2d-matrix.json");
        var builder = new StringBuilder(550);
        builder.Append("{\n  \"schema_version\": 1,\n  \"rows\": [\n");

        // Each row stores enabled contacts to the other 32 Unity layers.
        // Do not combine layer names/TagManager data with physical masks:
        // they control different compatibility surfaces.
        for (int first = 0; first < 32; first++)
        {
            uint mask = 0;
            for (int second = 0; second < 32; second++)
            {
                if (!Physics2D.GetIgnoreLayerCollision(first, second))
                {
                    mask |= 1u << second;
                }
            }
            builder.Append("    ");
            builder.Append(mask.ToString(CultureInfo.InvariantCulture));
            builder.Append(first == 31 ? "\n" : ",\n");
        }
        builder.Append("  ]\n}\n");
        // Stable deterministic output: editor timestamps and machine paths
        // must not make source-controlled gameplay assets differ.
        File.WriteAllText(output, builder.ToString(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log("Exported authoritative native Physics2D matrix: " + output);
    }
}
#endif
