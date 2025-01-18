
using Assets.Scripts.Scenes;
using System;
using UnityEngine;

namespace Assets.Scripts.Data
{
    public abstract class UserData
    {

        protected DataFile file = null;
        protected string defaultJsonData = "";
        private Action<dynamic> _onceDataIsLoaded;
        private bool _hasCalledAction;
        public UserData()
        {
        }
        protected dynamic SetupFile(bool shouldFileExist, string filename, Action<dynamic> onceDataIsLoaded)
        {
            _onceDataIsLoaded = onceDataIsLoaded;
            //Debug.Log("Called setup file");
            file = new DataFile(filename);
            dynamic json = null;
            // check if the file should already exist (which it should if this isn't the user's first time) and if it does in fact exist
            if (!file.Exists())
            {
                if (shouldFileExist)
                {
                    // throw error back to user if it does not, not because we can't make it but because it's missing
                    Debugger.Exception(new Exception("The user save data file is missing"));
                }
                Debug.Log($"DataFile {filename} doesn't exist");
            }
            else
            {
                //Debug.Log($"Datafile {filename} exists, reading from it");
                json = file.LoadJsonObject();

                //Debug.Log($"got json variable in UserData. Did not await {json}");
            }
            if (json == null || file.GetContents() == "")
            {
                //Debug.Log($"Datafile {filename} doesn't exist or is blank, writing default data");
                json = file.WriteData(defaultJsonData);
            }
            
            return json;
        }
        public DataFile GetDataFile()
        {
            return file;
        }
        public void Save()
        {
            GetDataFile().WriteData(ToJson());
        }
        public void WaitForData()
        {
            //Debug.Log("UserData is waiting for data");
            if (IsDataLoaded() && !_hasCalledAction)
            {
                _hasCalledAction = true;
                if (_onceDataIsLoaded != null)
                {
                    _onceDataIsLoaded(GetDataFile().GetJsonObject());
                }
            }
        }
        public bool IsDataLoaded()
        {
            return file.IsDataLoaded();
        }
        public string GetDefaultJson()
        {
            return defaultJsonData;
        }
        public abstract string ToJson();

    }
}