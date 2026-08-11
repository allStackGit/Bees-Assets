
using Newtonsoft.Json;
using System.IO;
using Assets.Scripts.Server;
using Assets.Scripts.Scenes;
using UnityEngine;
using System;

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
        private DataFileRequest _request = null;
        private bool _isDataLoaded = false;
        private ulong _userId;
        private readonly Action<string> _serverWriterOverride;

        public DataFile(string name)
        {
            this.Name = name;
            this.Path = ConfigData.GetBasePath();
            this.FullPath = System.IO.Path.Combine(Path, Name + Extension);
            _userId = ConfigData.GetUserId();
            //Debug.Log($"Full file path is {FullPath}");
        }

        /// <summary>
        /// Test/tooling constructor that keeps storage inside an explicitly owned directory
        /// and replaces the live socket write with a supplied transport callback.
        /// </summary>
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
                        //Debug.Log($"The standing request has completed, setting the contents: {standingRequest.Response.Contents}");
                        SetContents(standingRequest.Response.Contents);
                        _isDataLoaded = true;
                        return;
                    }
                    else if (standingRequest.Status == -1)
                    {
                        ConfigData.Socket.StandingRequests.Remove(standingRequest);

                        // A missing server file normally writes its defaults and then retries the
                        // read so the just-created row becomes authoritative. Dedicated training
                        // intentionally suppresses that persistent write; WriteData has already
                        // installed the defaults in memory, so retrying would loop forever on the
                        // still-missing server row. Accept the transient in-memory defaults instead.
                        if (global::HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime && _isDataLoaded)
                        {
                            _request = null;
                            return;
                        }

                        //Debug.Log($"The standing request has completed but needs to be resent");
                        ReadContents();
                        return;
                    }
                    else
                    {
                        //Debug.Log("Still waiting for datafile request to complete");
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
            object jsonObject = JsonConvert.DeserializeObject(contents);
            _textContents = contents;
            _jsonObject = jsonObject;
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
                return true; // if the file doesn't exist on the server, the socket handler will make the data file send the defaults
            }
            
        }
        public void WriteServerData(string data)
        {
            if (_serverWriterOverride != null)
            {
                _serverWriterOverride(data);
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
            // Some defaults depend on earlier asynchronously loaded identity data. The waiting
            // sentinel means "retry the read later", not user content; never persist it or mark
            // the file loaded.
            if (data == ConfigData.WaitingMessage)
            {
                return _jsonObject;
            }

            // Validate before any local or remote side effect. A malformed replacement
            // must not corrupt the last good file or be sent to the server.
            object jsonObject = JsonConvert.DeserializeObject(data);

            // Dedicated Hive Mind training may traverse ordinary player save/default code while
            // bootstrapping its runtime. Training state is disposable and must never mutate a
            // real profile, including the direct missing-file default writes issued by Socket.
            // Keep the data usable in memory without touching local disk or the server.
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