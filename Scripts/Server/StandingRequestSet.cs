using System;
using System.Collections;
using System.Collections.Generic;

namespace Assets.Scripts.Server
{
    /// <summary>
    /// Request collection keyed by the request's long transport hash without hash-table
    /// comparer dispatch. Unity WebGL/IL2CPP has failed both ServerRequest-keyed HashSet
    /// comparers and Dictionary<long, ServerRequest> insertion during startup, so this
    /// deliberately uses a small linear collection. Standing request counts are low and
    /// request-hash identity remains the contract.
    /// </summary>
    public class ServerRequestSet : IEnumerable<ServerRequest>
    {
        private readonly List<ServerRequest> _requests = new List<ServerRequest>();

        public int Count => _requests.Count;

        public bool Add(ServerRequest request)
        {
            if (request == null || FindIndexByHash(request.Hash) >= 0)
            {
                return false;
            }

            _requests.Add(request);
            return true;
        }

        public bool Remove(ServerRequest request)
        {
            if (request == null)
            {
                return false;
            }

            int index = FindIndexByHash(request.Hash);
            if (index < 0)
            {
                return false;
            }

            _requests.RemoveAt(index);
            return true;
        }

        public bool Contains(ServerRequest request)
        {
            return request != null && FindIndexByHash(request.Hash) >= 0;
        }

        public int RemoveWhere(Predicate<ServerRequest> match)
        {
            if (match == null)
            {
                throw new ArgumentNullException(nameof(match));
            }

            int removed = 0;
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                if (match(_requests[i]))
                {
                    _requests.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public void IntersectWith(IEnumerable<ServerRequest> requests)
        {
            if (requests == null)
            {
                throw new ArgumentNullException(nameof(requests));
            }

            List<long> retainedHashes = new List<long>();
            foreach (ServerRequest request in requests)
            {
                if (request != null && !ContainsHash(retainedHashes, request.Hash))
                {
                    retainedHashes.Add(request.Hash);
                }
            }

            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                if (!ContainsHash(retainedHashes, _requests[i].Hash))
                {
                    _requests.RemoveAt(i);
                }
            }
        }

        public void Clear()
        {
            _requests.Clear();
        }

        public bool TryGetByHash(long hash, out ServerRequest request)
        {
            int index = FindIndexByHash(hash);
            if (index >= 0)
            {
                request = _requests[index];
                return true;
            }

            request = null;
            return false;
        }

        private int FindIndexByHash(long hash)
        {
            for (int i = 0; i < _requests.Count; i++)
            {
                ServerRequest request = _requests[i];
                if (request != null && request.Hash == hash)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool ContainsHash(List<long> hashes, long hash)
        {
            for (int i = 0; i < hashes.Count; i++)
            {
                if (hashes[i] == hash)
                {
                    return true;
                }
            }
            return false;
        }

        public IEnumerator<ServerRequest> GetEnumerator()
        {
            return _requests.GetEnumerator();
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
