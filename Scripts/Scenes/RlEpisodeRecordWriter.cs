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

    // Preserve the episode header, winner/elimination and ship outcomes even with all
    // optional categories disabled. This record is diagnostic-only, so filtering it
    // cannot affect gameplay, delivered rewards or PPO trajectories.
    private static readonly string[] CombatSideFields =
    {
        "combat", "damage_by_source", "damage_by_ship_type", "self_damage",
        "friendly_damage", "unattributed_damage", "special_actions",
        "striker_reloads", "projectiles_hit"
    };
    private static readonly string[] EnvironmentSideFields =
    {
        "border_contacts", "static_contacts", "asteroid_contacts",
        "asteroid_damage", "asteroid_projectile_kills", "resources_mined"
    };
    private static readonly string[] AimFields =
    {
        "samples", "error_sum_deg", "within_5deg",
        "first_fire_distance", "first_hit_distance"
    };
    private static readonly string[] DecisionAimFields =
    {
        "requested_samples", "fresh_aim_samples", "held_aim_samples",
        "requested_error_sum_deg", "requested_within_5deg",
        "requested_within_15deg", "requested_within_30deg"
    };
    private static readonly string[] DirectionFields =
    {
        "target_bearing_sector_counts", "requested_bearing_sector_counts",
        "target_requested_sector_counts", "fresh_target_requested_sector_counts",
        "held_target_requested_sector_counts", "target_sector_error_sum_deg",
        "target_sector_within_15deg", "requested_bearing_cos_sum",
        "requested_bearing_sin_sum"
    };
    private static readonly string[] AimDecisionSections = { "policy_decisions", "stationary_decisions" };
    private static readonly string[] SideNames = { "bee", "human" };
    private static readonly string[] PolicyFields =
    {
        "team_id", "policy_role", "policy_version", "policy_version_source",
        "frame_quarter_turns", "movement_disabled",
        "policy_decisions", "with_known_enemy"
    };

    private static bool Includes(RlEpisodeLogCategory selected, RlEpisodeLogCategory category)
    {
        return (selected & category) != 0;
    }

    private static void RemoveFields(Dictionary<string, object> data, string[] fields)
    {
        foreach (string field in fields)
        {
            data.Remove(field);
        }
    }

    private static void FilterAim(
        Dictionary<string, object> side,
        RlEpisodeLogCategory categories)
    {
        if (!side.TryGetValue("aim", out object raw) ||
            !(raw is Dictionary<string, object> aim))
        {
            return;
        }

        if (!Includes(categories, RlEpisodeLogCategory.Aim))
        {
            RemoveFields(aim, AimFields);
        }

        foreach (string key in AimDecisionSections)
        {
            if (!aim.TryGetValue(key, out object section) ||
                !(section is Dictionary<string, object> decisions))
            {
                continue;
            }
            if (!Includes(categories, RlEpisodeLogCategory.Aim))
            {
                RemoveFields(decisions, DecisionAimFields);
            }
            if (!Includes(categories, RlEpisodeLogCategory.AimDirections))
            {
                RemoveFields(decisions, DirectionFields);
            }
            if (!Includes(categories, RlEpisodeLogCategory.Policy))
            {
                RemoveFields(decisions, PolicyFields);
            }
            if (decisions.Count == 0)
            {
                aim.Remove(key);
            }
        }

        if (aim.Count == 0)
        {
            side.Remove("aim");
        }
    }

    internal static void FilterCategories(
        Dictionary<string, object> episode,
        RlEpisodeLogCategory categories)
    {
        if (categories == RlEpisodeLogCategory.All)
        {
            return;
        }

        if (!Includes(categories, RlEpisodeLogCategory.Environment))
        {
            episode.Remove("environment");
        }

        foreach (string name in SideNames)
        {
            if (!episode.TryGetValue(name, out object raw) ||
                !(raw is Dictionary<string, object> side))
            {
                continue;
            }
            if (!Includes(categories, RlEpisodeLogCategory.Combat))
            {
                RemoveFields(side, CombatSideFields);
            }
            if (!Includes(categories, RlEpisodeLogCategory.Environment))
            {
                RemoveFields(side, EnvironmentSideFields);
            }
            if (!Includes(categories, RlEpisodeLogCategory.Visibility))
            {
                side.Remove("visibility");
            }
            if (!Includes(categories, RlEpisodeLogCategory.Rewards))
            {
                side.Remove("rewards");
                side.Remove("retained_mining_tsv");
            }
            FilterAim(side, categories);
        }
    }

    internal static void Write(Dictionary<string, object> episode)
    {
        FilterCategories(episode, RlOneVsOneTrainingBootstrap.CurrentEpisodeLogCategories);
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
