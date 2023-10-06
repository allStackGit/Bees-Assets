
using System;

namespace Assets.Scripts.Data
{
    public abstract class UserData
    {

        protected DataFile file = null;
        protected string defaultJsonData = "";
        private Action<dynamic> _onceDataIsLoaded;
        public UserData(bool shouldFileExist)
        {
        }
        protected dynamic SetupFile(bool shouldFileExist, string filename, Action<dynamic> onceDataIsLoaded)
        {
            _onceDataIsLoaded = onceDataIsLoaded;
            //Debugger.Log("Called setup file");
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
                //Debugger.Log("DataFile doesn't exist");
            }
            else
            {
                //Debugger.Log("Datafile exists, reading from it");
                json = file.LoadJsonObject();

                //Debugger.Log($"got json variable in UserData. Did not await {json}");
            }
            if (json == null || file.GetContents() == "")
            {
                //Debugger.Log("Datafile doesn't exist or is blank, writing default data"); 
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
            //Debugger.Log("UserData is waiting for data");
            if (IsDataLoaded())
            {
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