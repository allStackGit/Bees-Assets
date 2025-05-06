using UnityEditor;
using UnityEngine;
namespace Assets.Scripts.Server
{
    public class ServerResponse
    {
        public ConfigData.RequestTypes RequestType;
        public string Type;
        public int Status;
        public long Hash;
        public float ServerLatency;
        public int ProcessingTime; // [debug]
        public long SendTime; // [debug]

        public ServerResponse(string type, int status, long hash, float serverLatency)
        {
            Type = type;
            RequestType = Utilities.ConvertNameToRequestType[Type];
            Status = status;
            Hash = hash;
            ServerLatency = serverLatency;
            Debug.Log($"{this}, {type}");

        }

        private ServerResponse _response;
        public override bool Equals(System.Object obj)
        {
            if (obj == null)
            {
                return false;
            }

            // If parameter cannot be cast to class return false.
            _response = obj as ServerResponse;
            if (_response == null)
            {
                return false;
            }

            return Hash == _response.Hash;
        }

        public bool Equals(ServerRequest other)
        {
            return Hash == other.Hash;
        }

        public override int GetHashCode()
        {
            return Hash.GetHashCode();
        }

        public static bool operator ==(ServerResponse a, ServerResponse b)
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

        public static bool operator !=(ServerResponse a, ServerResponse b)
        {
            return !(a == b);
        }
        public override string ToString()
        {
            return $"SR #{Hash}:{RequestType} ({Status})";
        }
    }
}