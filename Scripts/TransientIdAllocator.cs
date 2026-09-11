using System;

namespace Assets.Scripts
{
    /// <summary>
    /// Allocates transient negative IDs without requiring player profile/fleet data.
    /// Normal gameplay preserves the historical Utilities ID behavior; dedicated/headless
    /// runtimes fall back to the process-unique hash generator when CurrentShips is absent.
    /// </summary>
    public static class TransientIdAllocator
    {
        public static long GetFleetShipId()
        {
            return ConfigData.CurrentShips != null
                ? Utilities.GetNegativeFleetshipId()
                : GetStandaloneNegativeId();
        }

        public static long GetSavedSquadId()
        {
            return ConfigData.CurrentShips != null
                ? Utilities.GetNegativeSavedSquadId()
                : GetStandaloneNegativeId();
        }

        private static long GetStandaloneNegativeId()
        {
            long id = Utilities.Hash();
            if (id == 0)
            {
                return -1;
            }
            if (id == long.MinValue)
            {
                return long.MinValue + 1;
            }
            return id < 0 ? id : -id;
        }
    }
}
