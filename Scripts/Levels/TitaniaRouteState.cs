using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Persists the outcome of Titania I so Titania II can reward a successful route-clearing
    /// mission without carrying the demolition maze itself into the base-defense battle.
    /// </summary>
    internal static class TitaniaRouteState
    {
        private const string ResultProperty = "TitaniaOneWon";
        private const string LegacyRouteProperty = "TitaniaOpenedBarrierPositions";

        private static bool _titaniaOneWon;
        private static bool _loaded;

        internal static bool DidWinTitaniaOne
        {
            get
            {
                EnsureLoaded();
                return _titaniaOneWon;
            }
        }

        internal static void RecordTitaniaOneResult(bool won)
        {
            _loaded = true;
            _titaniaOneWon = won;
        }

        internal static string AddToPlayerProgressJson(string userProgressJson)
        {
            EnsureLoaded();
            JObject progress = JObject.Parse(userProgressJson);

            // Route geometry was briefly persisted between Titania I and II. Titania II now starts
            // on a clear battlefield, so remove that legacy payload whenever the profile is saved.
            progress.Remove(LegacyRouteProperty);
            progress[ResultProperty] = _titaniaOneWon;
            return progress.ToString(Formatting.None);
        }

        internal static void LoadFromPlayerProgress(object loadedProgress)
        {
            _loaded = true;
            _titaniaOneWon = loadedProgress is JObject progress &&
                             progress[ResultProperty] != null &&
                             progress[ResultProperty].Value<bool>();
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
}
