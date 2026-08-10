using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assets.Scripts
{
    /// <summary>
    /// Stable comparer for pooled runtime wrappers whose logical Id changes between uses.
    /// Use this for cross-frame hash collections that store pooled objects by wrapper identity.
    /// </summary>
    public sealed class ReferenceIdentityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceIdentityComparer<T> Instance = new ReferenceIdentityComparer<T>();

        private ReferenceIdentityComparer()
        {
        }

        public bool Equals(T x, T y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
    }
}
