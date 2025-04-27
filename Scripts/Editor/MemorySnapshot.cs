using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using Unity.Profiling.Memory;
using UnityEditor.MemoryProfiler;

public class SafeMemorySnapshotTaker
{
    [MenuItem("Tools/Safe Memory Snapshot")]
    public static void TakeSafeSnapshot()
    {
        // Force full garbage collection to clean up stale memory
        Debug.Log("Starting GC...");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Debug.Log("GC Done.");

        // Create snapshot folder if needed
        string snapshotFolder = Path.Combine(Application.dataPath, "../MemoryCaptures");
        if (!Directory.Exists(snapshotFolder))
        {
            Directory.CreateDirectory(snapshotFolder);
        }

        // Create a timestamped file name
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string snapshotPath = Path.Combine(snapshotFolder, $"Snapshot_{timestamp}.snap");

        // Take snapshot using the Memory Profiler
        MemoryProfiler.TakeSnapshot(snapshotPath, (path, success) =>
        {
            if (success)
                Debug.Log($"Snapshot saved to: {path}");
            else
                Debug.LogError("Failed to save snapshot.");
        });
    }
}
