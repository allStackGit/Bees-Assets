using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Collections.Generic;

namespace Assets.Scripts.Entities.Ships
{
    public class Carrier : Ship
    {
        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (!IsDead)
            {
                List<Ship> levelShips = Level.State.Ships;
                Carrier replacementCarrier = null;
                for (int i = 0; i < levelShips.Count; i++)
                {
                    Ship candidate = levelShips[i];
                    if (candidate.Side == Side && candidate is Carrier carrier && carrier != this && !carrier.IsDead)
                    {
                        replacementCarrier = carrier;
                        break;
                    }
                }

                for (int i = 0; i < levelShips.Count; i++)
                {
                    Ship candidate = levelShips[i];
                    if (candidate.Side != Side || !(candidate is CarrierShip carrierShip) || carrierShip.Carrier != this)
                    {
                        continue;
                    }

                    if (replacementCarrier != null)
                    {
                        carrierShip.Carrier = replacementCarrier;
                    }
                    else
                    {
                        if (carrierShip is Striker striker)
                        {
                            striker.LastCarrierPosition = GetPosition();
                        }
                        carrierShip.Carrier = null;
                    }

                    if (carrierShip.Squad is CarrierSquad carrierSquad)
                    {
                        carrierSquad.Carrier = replacementCarrier;
                    }
                }
            }

            base.Kill(killer, killerFleetShip, killerSavedSquad, endKill);
        }
    }
}
