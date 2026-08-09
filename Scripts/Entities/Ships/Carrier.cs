using Assets.Scripts.Data;
using Assets.Scripts.Levels;
using System.Linq;

namespace Assets.Scripts.Entities.Ships
{
    public class Carrier : Ship
    {
        public override void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (!IsDead)
            {
                Carrier replacementCarrier = Level.State.GetShips(Side)
                    .OfType<Carrier>()
                    .FirstOrDefault(carrier => carrier != this && !carrier.IsDead);

                foreach (CarrierShip carrierShip in Level.State.GetShips(Side)
                    .OfType<CarrierShip>()
                    .Where(ship => ship.Carrier == this)
                    .ToList())
                {
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
