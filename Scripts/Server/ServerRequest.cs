
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
        public int TimeOnQueue = 0; // ms
        public int MaxTimeOnQueue; // ms
        public int Resends = 0;
        public long Hash = Utilities.Hash();
        public dynamic Request;

        public ServerRequest(int maxTimeOnQueue)
        {
            MaxTimeOnQueue = maxTimeOnQueue;
            Type = "server-request";
        }
    }
}