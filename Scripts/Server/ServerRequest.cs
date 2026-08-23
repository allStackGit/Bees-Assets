
using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// Base class for all server requests. This is used to track the status of requests and their responses.
    /// </summary>
    public abstract class ServerRequest
    {
        public ConfigData.RequestTypes Type;
        public int Status = 0;
        public long StartTime = ConfigData.Stopwatch.ElapsedMilliseconds;
        /// <summary>
        /// The amount of time this request has existed uncompleted in seconds. This is innacurate when used for average request time since it only counts completed requests.
        /// </summary>
        //public long TimeOnQueue = 0; // seconds [debug] 
        //public long SendTime; // [debug]
        public int MaxTimeOnQueue; // s
        public int Resends = 0;
        public long Hash = Utilities.Hash();
        public object Request;

        public ServerRequest(int maxTimeOnQueue)
        {
            // ConfigData.StandardMaxTimeOnQueue is the bootstrap default used before the
            // server configuration arrives. Requests that opt into that default should use
            // the server-owned value once Configuration has loaded.
            MaxTimeOnQueue = maxTimeOnQueue == ConfigData.StandardMaxTimeOnQueue
                ? Assets.Scripts.Settings.Configuration.GetStandardMaxTimeOnQueue()
                : maxTimeOnQueue;
            Type = ConfigData.RequestTypes.Request;
        }

        public bool HasExceededQueueTimeout(long currentTimeMilliseconds)
        {
            return currentTimeMilliseconds - StartTime > MaxTimeOnQueue * 1000L;
        }

        private ServerRequest _request;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            _request = obj as ServerRequest;
            if (_request == null)
            {
                return false;
            }

            return Hash == _request.Hash;
        }

        public bool Equals(ServerRequest other)
        {
            return Hash == other.Hash;
        }

        public override int GetHashCode()
        {
            return Hash.GetHashCode();
        }

        public static bool operator ==(ServerRequest a, ServerRequest b)
        {
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            if (((object)a == null) || ((object)b == null))
            {
                return false;
            }

            return a.Hash == b.Hash;
        }

        public static bool operator !=(ServerRequest a, ServerRequest b)
        {
            return !(a == b);
        }

        public override string ToString()
        {
            return $"SR #{Hash}:{Type}";
        }
    }
}
