using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
using System;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for user progress data
    public abstract class UserData
    {
        protected DataFile file = null;
        protected string defaultJsonData = "";
        private Action<object> _onceDataIsLoaded;
        private bool _hasCalledAction;
        private bool _hasFailedToLoad;
        protected string filename;

        public UserData()
        {
        }

        protected object SetupFile(bool shouldFileExist, string filename, Action<object> onceDataIsLoaded, bool forceCreateDefaults = false)
        {
            _onceDataIsLoaded = onceDataIsLoaded;
            this.filename = filename;
            file = new DataFile(filename);

            // Steam playtime/local first-run state cannot tell us whether a server-backed save
            // already exists. During normal startup always read remote storage first; the
            // get-user-data response path creates defaults only when the server explicitly reports
            // the file missing. Intentional reset operations opt out with forceCreateDefaults so
            // they cannot accidentally reload the remote state they are trying to replace.
            if (!ConfigData.Configuration.UseLocalStorage && !forceCreateDefaults)
            {
                shouldFileExist = true;
            }

            object json = null;
            if (!file.Exists())
            {
                if (shouldFileExist)
                {
                    Debug.LogError("The user save data file is missing");
                }
            }
            else if (shouldFileExist)
            {
                json = file.LoadJsonObject();
            }

            if (!shouldFileExist || json == null || file.GetContents() == "")
            {
                json = file.WriteData(GetDefaultJson());
            }

            return json;
        }

        public DataFile GetDataFile()
        {
            return file;
        }

        public void Save()
        {
            // Dedicated Hive Mind training reuses ordinary gameplay code paths that update
            // player counters at episode end. Those values are not training state and must not
            // be written back to a real profile. Fish Tank shares the same Unity scene but is
            // explicitly excluded by IsDedicatedTrainingRuntime.
            if (global::HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime)
            {
                return;
            }

            string data = ToJson();
            if (filename == ConfigData.UserProgressFilename)
            {
                data = TitaniaRouteState.AddToPlayerProgressJson(data);
            }
            GetDataFile().WriteData(data);
        }

        public void WaitForData()
        {
            if (!IsDataLoaded() || _hasCalledAction || _hasFailedToLoad)
            {
                return;
            }

            try
            {
                ApplyLoadedData();
            }
            catch (Exception error)
            {
                _hasFailedToLoad = true;
                MarkFailedLoadAsUnready();
                // A malformed/incompatible document is not a missing document. Replacing it
                // with defaults permanently destroys the original player data on disk/server.
                Debug.LogError($"Failed to load saved user data '{filename}'. Its contents have been preserved and startup will remain blocked rather than overwrite the profile. {error.GetType().Name}: {error.Message}");
            }
        }

        private void ApplyLoadedData()
        {
            // Object-rooted save files evolve as new settings/progress fields are added.
            // Overlay the existing save onto today's defaults so old saves inherit only
            // missing properties while preserving every value the user already stored.
            // Array-rooted formats (fleet/squad lists) intentionally pass through unchanged.
            object loadedData = GetLoadedDataWithDefaults();
            if (filename == ConfigData.UserProgressFilename)
            {
                TitaniaRouteState.LoadFromPlayerProgress(loadedData);
            }
            _onceDataIsLoaded?.Invoke(loadedData);
            _hasCalledAction = true;
        }

        private void MarkFailedLoadAsUnready()
        {
            // A callback may set its loaded flag before finishing deserialization. Never let
            // another scene finalize or a seven-file checkpoint write a partially loaded profile.
            if (filename == ConfigData.UserProgressFilename)
            {
                ConfigData.IsUserProgressDataLoaded = false;
                return;
            }
            if (filename == ConfigData.UserSettingsFilename)
            {
                ConfigData.IsUserSettingsDataLoaded = false;
                return;
            }

            for (int i = 0; i < ConfigData.FleetDataFilenames.Length; i++)
            {
                if (filename == ConfigData.FleetDataFilenames[i])
                {
                    ConfigData.IsFleetDataLoaded[i] = false;
                    return;
                }
                if (filename == ConfigData.SavedSquadsDataFilenames[i])
                {
                    ConfigData.IsSavedSquadsDataLoaded[i] = false;
                    return;
                }
                if (filename == ConfigData.LevelsDataFilenames[i])
                {
                    ConfigData.IsLevelsDataLoaded[i] = false;
                    return;
                }
            }
        }

        private object GetLoadedDataWithDefaults()
        {
            object loadedObject = GetDataFile().GetJsonObject();
            if (!(loadedObject is JObject loaded) || string.IsNullOrWhiteSpace(defaultJsonData))
            {
                return loadedObject;
            }

            JToken defaultToken = JToken.Parse(defaultJsonData);
            if (!(defaultToken is JObject defaults))
            {
                return loadedObject;
            }

            defaults.Merge(loaded, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace
            });
            return defaults;
        }

        public virtual bool IsDataLoaded()
        {
            return file.IsDataLoaded();
        }

        public virtual string GetDefaultJson()
        {
            return defaultJsonData;
        }

        public abstract string ToJson();
    }
}
