using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// One self-contained JSONL record per completed RL episode. Files are append-only and
/// rotated between records. Closed segments are retained for historical analysis.
/// </summary>
internal static class RlEpisodeRecordWriter
{
    private const long MaxSegmentBytes = 8L * 1024L * 1024L;
    private static readonly object WriteLock = new object();
    private static readonly Encoding Encoding = new UTF8Encoding(false);
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static int _segment;
    private static bool _warned;

    internal static void Write(Dictionary<string, object> episode)
    {
        episode["utc"] = DateTime.UtcNow.ToString("o");
        string line = JsonConvert.SerializeObject(episode, Formatting.None);
        string root = Environment.GetEnvironmentVariable("BEES_TRAINING_LOG_DIR");
        if (string.IsNullOrWhiteSpace(root))
        {
            Debug.Log(line);
            return;
        }

        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(root);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string path = Path.Combine(root, $"BeesEpisode-{pid}-{SessionId}-{_segment:D5}.jsonl");
                long addedBytes = Encoding.GetByteCount(line) + Encoding.GetByteCount(Environment.NewLine);
                if (File.Exists(path) && new FileInfo(path).Length + addedBytes > MaxSegmentBytes)
                {
                    _segment++;
                    path = Path.Combine(root, $"BeesEpisode-{pid}-{SessionId}-{_segment:D5}.jsonl");
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding);
            }
        }
        catch (Exception exception)
        {
            if (!_warned)
            {
                _warned = true;
                Debug.LogWarning("RL episode JSONL write failed; falling back to Unity log: " + exception.Message);
            }
            Debug.Log(line);
        }
    }
}
