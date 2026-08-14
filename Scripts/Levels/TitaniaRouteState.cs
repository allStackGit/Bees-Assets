using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Persists Titania campaign outcomes that affect the following mission or intermission.
    /// Titania I controls Beenoculars duration; Titania II controls whether A.M.I. survived.
    /// </summary>
    internal static class TitaniaRouteState
    {
        private const string TitaniaOneResultProperty = "TitaniaOneWon";
        private const string TitaniaTwoResultProperty = "TitaniaTwoWon";
        private const string LegacyRouteProperty = "TitaniaOpenedBarrierPositions";

        private static bool _titaniaOneWon;
        private static bool _titaniaTwoWon;
        private static bool _loaded;

        internal static bool DidWinTitaniaOne
        {
            get
            {
                EnsureLoaded();
                return _titaniaOneWon;
            }
        }

        internal static bool DidWinTitaniaTwo
        {
            get
            {
                EnsureLoaded();
                return _titaniaTwoWon;
            }
        }

        internal static void RecordTitaniaOneResult(bool won)
        {
            _loaded = true;
            _titaniaOneWon = won;
            // Starting a new Titania sequence invalidates any stale Beenoculars result.
            _titaniaTwoWon = false;
        }

        internal static void RecordTitaniaTwoResult(bool won)
        {
            _loaded = true;
            _titaniaTwoWon = won;
        }

        internal static string AddToPlayerProgressJson(string userProgressJson)
        {
            EnsureLoaded();
            JObject progress = JObject.Parse(userProgressJson);

            // Route geometry was briefly persisted between Titania I and II. Titania II now uses
            // its own authored obstacle field, so remove that legacy payload whenever saved.
            progress.Remove(LegacyRouteProperty);
            progress[TitaniaOneResultProperty] = _titaniaOneWon;
            progress[TitaniaTwoResultProperty] = _titaniaTwoWon;
            return progress.ToString(Formatting.None);
        }

        internal static void LoadFromPlayerProgress(object loadedProgress)
        {
            _loaded = true;
            if (loadedProgress is not JObject progress)
            {
                _titaniaOneWon = false;
                _titaniaTwoWon = false;
                return;
            }

            _titaniaOneWon = progress[TitaniaOneResultProperty] != null &&
                             progress[TitaniaOneResultProperty].Value<bool>();
            _titaniaTwoWon = progress[TitaniaTwoResultProperty] != null &&
                             progress[TitaniaTwoResultProperty].Value<bool>();
        }

        private static void EnsureLoaded()
        {
            if (_loaded || ConfigData.UserProgressData == null || !ConfigData.IsUserProgressDataLoaded)
            {
                return;
            }

            LoadFromPlayerProgress(ConfigData.UserProgressData.GetDataFile().GetJsonObject());
        }
    }

    /// <summary>
    /// Beenoculars resolves its winner before playing the final dialogue and checkpointing the
    /// campaign. Capture that result during the dialogue window so the following Uranus
    /// intermission can conditionally include A.M.I. exactly as authored.
    /// </summary>
    [DefaultExecutionOrder(9000)]
    internal sealed class TitaniaOutcomePersistenceGuard : MonoBehaviour
    {
        private Level _recordedLevel;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Titania Outcome Persistence Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<TitaniaOutcomePersistenceGuard>();
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || ReferenceEquals(level, _recordedLevel) ||
                    level.CurrentLevelOptions == null || level.CurrentLevelOptions.Id != 8 ||
                    level.WinningSide == 0)
                {
                    continue;
                }

                TitaniaRouteState.RecordTitaniaTwoResult(
                    level.WinningSide == ConfigData.Configuration.UserSide);
                _recordedLevel = level;
            }
        }
    }
}
