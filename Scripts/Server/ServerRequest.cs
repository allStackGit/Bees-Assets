
using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public abstract class ServerRequest
    {
        public ConfigData.RequestTypes Type;
        public int Status = 0;
        public float StartTime = Time.unscaledTime;
        /// <summary>
        /// The amount of time this request has existed uncompleted in seconds
        /// </summary>
        public float TimeOnQueue = 0; // s
        public int MaxTimeOnQueue; // s
        public int Resends = 0;
        public int Hash = Utilities.Hash();
        public dynamic Request;

        public ServerRequest(int maxTimeOnQueue)
        {
            MaxTimeOnQueue = maxTimeOnQueue;
            Type = ConfigData.RequestTypes.Request;
        }

        public bool Equals(ServerRequest sr)
        {
            return sr.Hash == Hash;
        }

        public override int GetHashCode()
        {
            return Hash;
        }
        public override string ToString()
        {
            return $"SR #{Hash}:{Type}";
        }
    }
}