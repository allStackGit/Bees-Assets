
using Newtonsoft.Json;
using System.IO;
using Assets.Scripts.Server;
using Assets.Scripts.Scenes;
using UnityEngine;
using System;

namespace Assets.Scripts.Data
{
    public class DataFile
    {
        public readonly string Name;
        public readonly string Path;
        public const string Extension = ".json";
        public readonly string FullPath;

        private string _textContents;
        private object _jsonObject;
        private DataFileRequest _request = null;
        private bool _isDataLoaded = false;
        private ulong _userId;
        private readonly Action<string> _serverWriterOverride;

        /// <summary>
        /// True only when a server-backed read was already in flight and the missing-data
        /// response path created this file from defaults. Existing remote rows, including old
        /// profiles whose PlayerName happens to be blank, remain false.
        /// </summary>
        public bool WasCreatedFromMissingStorage { get; private set; }

        public DataFile(string name)
        {
            this.Name = name;
            this.Path = ConfigData.GetBasePath();
            this.FullPath = System.IO.Path.Combine(Path, Name + Extension);
            _userId = ConfigData.GetUserId();
        }

        public DataFile(string name, string basePath, ulong userId, Action<string> serverWriter)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A data-file name is required.", nameof(name));
            }
            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw new ArgumentException("A data-file base path is required.", nameof(basePath));
            }

            Name = name;
            Path = basePath;
            FullPath = System.IO.Path.Combine(Path, Name + Extension);
            _userId = userId;
            _serverWriterOverride = serverWriter;
        }
        private void MakeFileIfNecessary()
        {
            Directory.CreateDirectory(Path);
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
                _request = new DataFileRequest(new GetUserData(_userId, Name), this, ConfigData.StandardMaxTimeOnQueue);
                ConfigData.Socket.SendRequest(_request);
                contents = ConfigData.WaitingMessage;
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
                if (standingRequest != null)
                {
                    if (standingRequest.Status == 1)
                    {
                        ConfigData.Socket.StandingRequests.Remove(standingRequest);
                        SetContents(standingRequest.Response.Contents);
                        _isDataLoaded = true;
                        _request = null;
                        return;
                    }
                    else if (standingRequest.Status == -1)
                    {
                        ConfigData.Socket.StandingRequests.Remove(standingRequest);

                        // HandleUserDataResponse creates defaults before marking a genuinely missing
                        // row with Status -1. Profile-member writes are coalesced until every member
                        // is ready, so re-reading here would ask for the same still-missing row
                        // forever and prevent the checkpoint from ever becoming flushable. Once the
                        // fallback is loaded locally, accept it as the terminal read result; the
                        // completed profile checkpoint will persist it after bootstrap finishes.
                        if (WasCreatedFromMissingStorage && _isDataLoaded &&
                            _textContents != ConfigData.WaitingMessage)
                        {
                            _request = null;
                            return;
                        }

                        if (global::HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime && _isDataLoaded)
                        {
                            _request = null;
                            return;
                        }

                        ReadContents();
                        return;
                    }
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
            try
            {
                _jsonObject = JsonConvert.DeserializeObject(contents);
            }
            catch (JsonException exception)
            {
                // Server-backed legacy profiles can contain syntactically malformed JSON. Preserve
                // the raw payload long enough for UserData.WaitForData to classify the file as
                // incompatible and rebuild it from defaults instead of throwing out of Socket.Update
                // and leaving the whole main-menu bootstrap permanently unfinished.
                _jsonObject = null;
                Debug.LogError($"Stored user data '{Name}' contains malformed JSON and will be recovered. {exception.GetType().Name}: {exception.Message}");
            }
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
            return _isDataLoaded && _textContents != ConfigData.WaitingMessage;
        }
        public bool Exists()
        {   
            if (ConfigData.Configuration.UseLocalStorage)
            {
                return File.Exists(FullPath);
            }
            else
            {
                return true;
            }
        }
        public void WriteServerData(string data)
        {
            if (_serverWriterOverride != null)
            {
                _serverWriterOverride(data);
                return;
            }

            if (!ConfigData.Test && global::Assets.Scripts.CampaignCheckpoint.IsProfileMember(Name))
            {
                global::Assets.Scripts.CampaignCheckpoint.Save();
                return;
            }

            ConfigData.Socket.SendRequest(new StoreUserDataRequest(new StoreUserData(_userId, Name, data),
                ConfigData.StandardMaxTimeOnQueue));
        }
        public void WriteLocalData(string data)
        {
            MakeFileIfNecessary();
            File.WriteAllText(FullPath, data);
        }
        public object WriteData(string data)
        {
            if (data == ConfigData.WaitingMessage)
            {
                return _jsonObject;
            }

            object jsonObject = JsonConvert.DeserializeObject(data);

            // For remote storage, the only write that can occur while the initial get-user-data
            // request is still unresolved is the client's missing-row fallback. Remember that
            // distinction so UI can tell a genuinely new profile from an existing blank profile.
            if (!ConfigData.Configuration.UseLocalStorage && _request != null && !_isDataLoaded)
            {
                WasCreatedFromMissingStorage = true;
            }

            if (global::HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime)
            {
                _textContents = data;
                _jsonObject = jsonObject;
                _isDataLoaded = true;
                return GetJsonObject();
            }

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
            _jsonObject = jsonObject;
            _isDataLoaded = true;
            return GetJsonObject();
        }
    }
}
