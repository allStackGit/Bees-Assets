
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
        public long Hash = Utilities.Hash();
        public dynamic Request;

        public ServerRequest(int maxTimeOnQueue)
        {
            MaxTimeOnQueue = maxTimeOnQueue;
            Type = ConfigData.RequestTypes.Request;
        }

        private ServerRequest _request;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
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
            // If both are null, or both are same instance, return true.
            if (System.Object.ReferenceEquals(a, b))
            {
                return true;
            }

            // If one is null, but not both, return false.
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