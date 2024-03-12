
using Newtonsoft.Json;
using System.IO;
using Assets.Scripts.Server;
using Assets.Scripts.Scenes;
using UnityEngine;

namespace Assets.Scripts.Data
{
    // stores user data as JSON either on a local file or in the server
    public class DataFile
    {
        public readonly string Name;
        public readonly string Path;
        public const string Extension = ".json";
        public readonly string FullPath;

        private string _textContents;
        private object _jsonObject;
        private const string _waitingMessage = "{\"status\": \"waiting\"}";
        private DataFileRequest _request = null;
        private bool _isDataLoaded = false;

        public DataFile(string name)
        {
            this.Name = name;
            this.Path = ConfigData.GetBasePath();
            this.FullPath = $"{Path}{Name}{Extension}";
            //Debug.Log($"Full file path is {FullPath}");
        }
        private void MakeFileIfNecessary()
        {
            // make file if it does not exist
            if (!Exists())
            {
                FileStream fs = File.Create(FullPath);
                fs.Close();
            }
        }
        private string ReadContents()
        {
            string contents = "";
            if (ConfigData.Configuration.UseLocalStorage)
            {
                MakeFileIfNecessary();
                StreamReader fileStream = new StreamReader(FullPath);

                while (!fileStream.EndOfStream)
                {
                    string line = fileStream.ReadLine();
                    contents += line;
                }

                fileStream.Close();
            }
            else
            {
                _request = new DataFileRequest(new GetUserData(ConfigData.GetUserId(), Name), this, ConfigData.StandardMaxTimeOnQueue);
                ConfigData.Socket.SendRequest(_request);
                contents = _waitingMessage;
            }
            
            SetContents(contents);
            return contents;
        }
        private object ReadJsonObject()
        {
            string contents = ReadContents();
            if (!ConfigData.Configuration.UseLocalStorage)
            {
                contents = GetContents();
            }
            return JsonConvert.DeserializeObject(contents);
        }

        public void WaitForResponse()
        {
            if (_request != null)
            {
                DataFileRequest standingRequest = (DataFileRequest)ConfigData.Socket.GetStandingRequest(_request.Hash);
                if (standingRequest.Status == 1)
                {
                    ConfigData.Socket.StandingRequests.Remove(standingRequest);
                    //Debug.Log($"The standing request has completed, setting the contents: {standingRequest.Response.Contents}");
                    SetContents(standingRequest.Response.Contents);
                    _isDataLoaded = true;
                    return;
                }
                else if (standingRequest.Status == -1)
                {
                    Debug.Log($"The standing request has completed but needs to be resent");
                    ReadContents();
                    return;
                }
                else
                {
                    //Debug.Log("Still waiting for datafile request to complete");
                }
            }

        }
        public string GetContents()
        {
            return _textContents;
        }
        public DataFileRequest GetRequest()
        {
            return _request;
        }
        public void SetContents(string contents)
        {
            _textContents = contents;
            _jsonObject = JsonConvert.DeserializeObject(contents);
        }
        public object GetJsonObject()
        {
            return _jsonObject;
        }
        public dynamic LoadJsonObject()
        {
            _jsonObject = ReadJsonObject();
            if (ConfigData.Configuration.UseLocalStorage)
            {
                _isDataLoaded = true;
            }
            return _jsonObject;
        }
        public bool IsDataLoaded()
        {
            return _isDataLoaded;
        }
        public bool Exists()
        {   
            if (ConfigData.Configuration.UseLocalStorage)
            {
                return File.Exists(FullPath);
            }
            else
            {
                return true; // if the file doesn't exist on the server, the socket handler will make the data file send the defaults
            }
            
        }
        public void WriteServerData(string data)
        {
            ConfigData.Socket.SendRequest(new StoreUserDataRequest(new StoreUserData(ConfigData.GetUserId(), Name, data),
                ConfigData.StandardMaxTimeOnQueue));
        }
        public void WriteLocalData(string data)
        {
            MakeFileIfNecessary();
            File.WriteAllText(FullPath, data);
        }
        public object WriteData(string data)
        {
            if (ConfigData.Configuration.UseLocalStorage)
            {
                WriteLocalData(data);
                if (ConfigData.Configuration.MirrorLocalStorageToServer)
                {
                    WriteServerData(data);
                }
            }
            else
            {
                WriteServerData(data);
                if (ConfigData.Configuration.MirrorServerStorageToLocal)
                {
                    WriteLocalData(data);
                }
            }
            _textContents = data;
            _jsonObject = JsonConvert.DeserializeObject(data);
            _isDataLoaded = true;
            return GetJsonObject();

        }
    }
}