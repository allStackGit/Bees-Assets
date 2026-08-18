using System;
using System.Collections;
using System.Collections.Generic;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// Request collection keyed directly by the request's long transport hash.
    /// Avoiding HashSet&lt;ServerRequest&gt; keeps IL2CPP/WebGL from routing request tracking
    /// through a generated IEqualityComparer&lt;ServerRequest&gt; implementation while preserving
    /// the existing hash-based set semantics.
    /// </summary>
    public class ServerRequestSet : IEnumerable<ServerRequest>
    {
        private readonly Dictionary<long, ServerRequest> _requestsByHash = new Dictionary<long, ServerRequest>();
        private readonly List<long> _removeHashBuffer = new List<long>();
        private readonly HashSet<long> _intersectionHashes = new HashSet<long>();

        public int Count => _requestsByHash.Count;

        public bool Add(ServerRequest request)
        {
            if (request == null || _requestsByHash.ContainsKey(request.Hash))
            {
                return false;
            }

            _requestsByHash.Add(request.Hash, request);
            return true;
        }

        public bool Remove(ServerRequest request)
        {
            return request != null && _requestsByHash.Remove(request.Hash);
        }

        public bool Contains(ServerRequest request)
        {
            return request != null && _requestsByHash.ContainsKey(request.Hash);
        }

        public int RemoveWhere(Predicate<ServerRequest> match)
        {
            if (match == null)
            {
                throw new ArgumentNullException(nameof(match));
            }

            _removeHashBuffer.Clear();
            foreach (KeyValuePair<long, ServerRequest> entry in _requestsByHash)
            {
                if (match(entry.Value))
                {
                    _removeHashBuffer.Add(entry.Key);
                }
            }

            int removed = _removeHashBuffer.Count;
            for (int i = 0; i < _removeHashBuffer.Count; i++)
            {
                _requestsByHash.Remove(_removeHashBuffer[i]);
            }
            _removeHashBuffer.Clear();
            return removed;
        }

        public void IntersectWith(IEnumerable<ServerRequest> requests)
        {
            if (requests == null)
            {
                throw new ArgumentNullException(nameof(requests));
            }

            _intersectionHashes.Clear();
            foreach (ServerRequest request in requests)
            {
                if (request != null)
                {
                    _intersectionHashes.Add(request.Hash);
                }
            }

            _removeHashBuffer.Clear();
            foreach (long hash in _requestsByHash.Keys)
            {
                if (!_intersectionHashes.Contains(hash))
                {
                    _removeHashBuffer.Add(hash);
                }
            }

            for (int i = 0; i < _removeHashBuffer.Count; i++)
            {
                _requestsByHash.Remove(_removeHashBuffer[i]);
            }
            _removeHashBuffer.Clear();
            _intersectionHashes.Clear();
        }

        public void Clear()
        {
            _requestsByHash.Clear();
            _removeHashBuffer.Clear();
            _intersectionHashes.Clear();
        }

        public bool TryGetByHash(long hash, out ServerRequest request)
        {
            return _requestsByHash.TryGetValue(hash, out request);
        }

        public IEnumerator<ServerRequest> GetEnumerator()
        {
            return _requestsByHash.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>
    /// Named standing-request collection retained for Socket's public API and tests.
    /// </summary>
    public sealed class StandingRequestSet : ServerRequestSet
    {
    }
}
