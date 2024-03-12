
using System;
using System.Collections;
using UnityEngine;

namespace Assets.Scripts.Server
{
    public abstract class ServerRequest
    {
        public string Type;
        public int Status = 0;
        public float StartTime = Time.unscaledTime;
        public float TimeOnQueue = 0; // s
        public int MaxTimeOnQueue; // s
        public int Resends = 0;
        public int Hash = Utilities.Hash();
        public dynamic Request;

        public ServerRequest(int maxTimeOnQueue)
        {
            MaxTimeOnQueue = maxTimeOnQueue;
            Type = "server-request";
        }

        public bool Equals(ServerRequest sr)
        {
            return sr.Hash == Hash;
        }

        public override int GetHashCode()
        {
            return Hash;
        }
    }
}