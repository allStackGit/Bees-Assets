using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels.Commands;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class FullRetreat : Command
{
    public WarpGate TargetWarpGate;
    public List<Ship> ShipsWaitingToWarp = new List<Ship>();
    public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, WarpGate warpgate)
    {
        if (warpgate != null)
        {
            TargetWarpGate = warpgate;
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            
            // The ToList() is necessary to prevent errors from warp killing while looping through the list of ships
            Squad.GetShips().ToList().ForEach((ship) =>
            {
                if (ship.ShipType != ConfigData.ShipTypes.WarpGate)
                {
                    TargetWarpGate.ShipsWarpingHere.Add(ship);
                    if (ship.Collider.IsTouching(TargetWarpGate.WarpCollider))
                    {
                        ShipsWaitingToWarp.Add(ship);
                        WaitToWarp();
                    }
                }

            });

            if (TargetWarpGate.ShipsWarpingHere.Count > 0)
            {
                TargetWarpGate.ShipAnimation.SetActive(true);
            }
            else
            {
                SetFinalize("The only ships in this squad are Warp Gates");
                return;
            }

            PrepareDamageToSendEntries("closest");
            InvokeRepeating(nameof(MoveToWarpGate), 0, CommandFrequency);
        }
        else
        {
            SetFinalize("The warp gate doesn't exist anymore, or there were no warp gates around");
        }

    }

    public void MoveToWarpGate()
    {
        if (!Squad.IsDead && !TargetWarpGate.IsDead)
        {
            Vector2 targetPosition = TargetWarpGate.GetPosition() + TargetWarpGate.WarpPoint;
            Squad.GetShips().ForEach((ship) =>
            {
                if (ship.ShipType != ConfigData.ShipTypes.WarpGate)
                {
                    ship.MoveToPoint(targetPosition);
                }
            });
            Squad.Status = $"Moving to {TargetWarpGate.Name} to warp out of the level: {targetPosition}";
        }
        else if (TargetWarpGate.IsDead)
        {
            SetFinalize("Warp gate was destroyed");
        }
    }

    public void WaitToWarp()
    {
        if (TargetWarpGate.ShipAnimationController.IsReadyToWarp)
        {
            ShipsWaitingToWarp.ForEach((ship) =>
            {
                WarpKill(ship);
            });
            ShipsWaitingToWarp.Clear();
        }
        else
        {
            Invoke(nameof(WaitToWarp), 2);
        }
    }

    public void WarpKill(Ship ship)
    {
        Tsv += (int)(ship.Tsv * .05f);
        TargetWarpGate.ShipsWarpingHere.Remove(ship);
        ship.EndKill();
        if (TargetWarpGate.ShipsWarpingHere.Count == 0)
        {
            SetFinalize("All ships have warped");
        }
    }

    public void CleanupWarpGate()
    {
        //Debug.Log($"Clenaing up warp gate: {TargetWarpGate}");
        if (TargetWarpGate != null && !TargetWarpGate.IsDead)
        {
            if (!Squad.IsDead)
            {
                Squad.GetShips().ForEach((ship) =>
                {
                    TargetWarpGate.ShipsWarpingHere.Remove(ship);
                });
            }
            else
            {
                TargetWarpGate.ShipsWarpingHere.RemoveWhere((s) =>
                {
                    return s == null || s.IsDead;
                });
            }

            if (TargetWarpGate.ShipsWarpingHere.Count == 0)
            {
                TargetWarpGate.ShipAnimation.SetActive(false);
                TargetWarpGate.ShipAnimationController.UseSecondaryLoop = false;
                TargetWarpGate.ShipAnimationController.IsReadyToWarp = false;
                TargetWarpGate.ShipAnimationController.SpriteIndex = 0;
            }
        }
        
    }
    public override void SetFinalize(string cause)
    {
        //Debug.Log($"Finalizing full retreat command for {Squad.Name}");
        CleanupWarpGate();
        base.SetFinalize(cause);
    }
}
