using System;
using System.Collections.Generic;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// HashSet-compatible standing-request collection with a hash index for response lookup.
    /// The public set semantics are preserved because several runtime and test paths enumerate
    /// or directly add/remove requests through Socket.StandingRequests.
    /// </summary>
    public sealed class StandingRequestSet : HashSet<ServerRequest>
    {
        private readonly Dictionary<long, ServerRequest> _requestsByHash = new Dictionary<long, ServerRequest>();
        private readonly List<ServerRequest> _removeBuffer = new List<ServerRequest>();

        public new bool Add(ServerRequest request)
        {
            if (request == null)
            {
                return false;
            }

            bool added = base.Add(request);
            if (added)
            {
                _requestsByHash[request.Hash] = request;
            }
            return added;
        }

        public new bool Remove(ServerRequest request)
        {
            if (request == null)
            {
                return false;
            }

            bool removed = base.Remove(request);
            if (_requestsByHash.TryGetValue(request.Hash, out ServerRequest indexedRequest) &&
                ReferenceEquals(indexedRequest, request))
            {
                _requestsByHash.Remove(request.Hash);
            }
            return removed;
        }

        public new int RemoveWhere(Predicate<ServerRequest> match)
        {
            if (match == null)
            {
                throw new ArgumentNullException(nameof(match));
            }

            _removeBuffer.Clear();
            foreach (ServerRequest request in this)
            {
                if (match(request))
                {
                    _removeBuffer.Add(request);
                }
            }

            int removed = 0;
            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                if (Remove(_removeBuffer[i]))
                {
                    removed++;
                }
            }
            _removeBuffer.Clear();
            return removed;
        }

        public new void Clear()
        {
            base.Clear();
            _requestsByHash.Clear();
            _removeBuffer.Clear();
        }

        public bool TryGetByHash(long hash, out ServerRequest request)
        {
            if (_requestsByHash.TryGetValue(hash, out request) &&
                request != null &&
                request.Hash == hash)
            {
                return true;
            }

            // Defensive fallback for callers/tests that obtained the collection through a base
            // interface or otherwise bypassed the hidden Add method. Normal production lookups
            // remain O(1); a fallback lookup repairs the index for subsequent responses.
            _requestsByHash.Remove(hash);
            foreach (ServerRequest candidate in this)
            {
                if (candidate != null && candidate.Hash == hash)
                {
                    _requestsByHash[hash] = candidate;
                    request = candidate;
                    return true;
                }
            }

            request = null;
            return false;
        }
    }
}
