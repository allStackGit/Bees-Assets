
using Assets.Scripts.Levels;
using Assets.Scripts.Scenes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // class that holds and manages storage for user progress data
    public abstract class UserData
    {

        protected DataFile file = null;
        protected string defaultJsonData = "";
        private Action<dynamic> _onceDataIsLoaded;
        private bool _hasCalledAction;
        private bool _hasAttemptedMalformedRecovery;
        private bool _hasLoggedMalformedRecoveryFailure;
        protected string filename;
        public UserData()
        {
        }
        protected dynamic SetupFile(bool shouldFileExist, string filename, Action<dynamic> onceDataIsLoaded, bool forceCreateDefaults = false)
        {
            _onceDataIsLoaded = onceDataIsLoaded;
            //Debug.Log("Called setup file");
            this.filename = filename;
            this.file = new DataFile(filename);

            // Steam playtime/local first-run state cannot tell us whether a server-backed save
            // already exists. During normal startup always read remote storage first; the
            // get-user-data response path creates defaults only when the server explicitly reports
            // the file missing. Intentional reset operations opt out with forceCreateDefaults so
            // they cannot accidentally reload the remote state they are trying to replace.
            if (!ConfigData.Configuration.UseLocalStorage && !forceCreateDefaults)
            {
                shouldFileExist = true;
            }

            dynamic json = null;
            // check if the file should already exist (which it should if this isn't the user's first time) and if it does in fact exist
            if (!file.Exists())
            {
                if (shouldFileExist)
                {
                    // throw error back to user if it does not, not because we can't make it but because it's missing
                    Debug.LogError("The user save data file is missing");
                }
                Debug.Log($"DataFile {filename} doesn't exist");
            }
            else if (shouldFileExist)
            {
                //Debug.Log($"Datafile {filename} exists, reading from it");
                json = file.LoadJsonObject();

                //Debug.Log($"got json variable in UserData. Did not await {json}");
            }
            if (!shouldFileExist || json == null || file.GetContents() == "")
            {
                //Debug.Log($"Datafile {filename} doesn't exist or is blank, writing default data");
                json = this.file.WriteData(GetDefaultJson());
            }
            //else
            //{
            //    Debug.LogError($"shouldFileExist {shouldFileExist}, json {json}, file.GetContents() {file.GetContents()}");
            //}

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
            if (!IsDataLoaded() || _hasCalledAction)
            {
                return;
            }

            try
            {
                ApplyLoadedData();
            }
            catch (Exception error)
            {
                RecoverMalformedData(error);
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

        private void RecoverMalformedData(Exception originalError)
        {
            if (_hasAttemptedMalformedRecovery)
            {
                if (!_hasLoggedMalformedRecoveryFailure)
                {
                    _hasLoggedMalformedRecoveryFailure = true;
                    Debug.LogError($"Could not load repaired user data '{filename}'. Startup will keep the data unavailable instead of applying a partial profile. {originalError.GetType().Name}: {originalError.Message}");
                }
                return;
            }

            _hasAttemptedMalformedRecovery = true;
            string defaults;
            try
            {
                defaults = GetDefaultJson();
            }
            catch (Exception defaultError)
            {
                Debug.LogError($"Could not create recovery defaults for user data '{filename}'. {defaultError.GetType().Name}: {defaultError.Message}");
                return;
            }

            // Fleet defaults can temporarily depend on user_progress arriving first. In that case
            // leave recovery eligible for the next readiness pass rather than freezing the file in
            // a failed state.
            if (string.IsNullOrWhiteSpace(defaults) || defaults == ConfigData.WaitingMessage)
            {
                _hasAttemptedMalformedRecovery = false;
                return;
            }

            Debug.LogError($"User data '{filename}' is malformed or incompatible and will be rebuilt from safe defaults. {originalError.GetType().Name}: {originalError.Message}");
            file.WriteData(defaults);

            try
            {
                ApplyLoadedData();
            }
            catch (Exception recoveryError)
            {
                if (!_hasLoggedMalformedRecoveryFailure)
                {
                    _hasLoggedMalformedRecoveryFailure = true;
                    Debug.LogError($"Default recovery also failed for user data '{filename}'. {recoveryError.GetType().Name}: {recoveryError.Message}");
                }
            }
        }
        private dynamic GetLoadedDataWithDefaults()
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