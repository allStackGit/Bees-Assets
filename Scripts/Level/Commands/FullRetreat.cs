using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Level.Commands;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FullRetreat : Command
{
    public WarpGate TargetWarpGate;
    public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, WarpGate warpgate)
    {
        if (warpgate != null)
        {
            TargetWarpGate = warpgate;
            base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);

            Squad.GetShips().ForEach((ship) =>
            {
                if (ship.ShipType != "Warp Gate")
                {
                    TargetWarpGate.ShipsWarpingHere.Add(ship);
                    if (ship.Collider.IsTouching(TargetWarpGate.WarpCollider))
                    {
                        WarpKill(ship);
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
        if (!Squad.IsDead && !TargetWarpGate.IsDead && TargetWarpGate.ShipAnimationController.IsReadyToWarp)
        {
            Vector2 targetPosition = TargetWarpGate.GetPosition() + TargetWarpGate.WarpPoint;
            Squad.GetShips().ForEach((ship) =>
            {
                if (ship.ShipType != "Warp Gate")
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

    public void WarpKill(Ship ship)
    {
        Tsv += (int)(ship.Tsv * .05f);
        TargetWarpGate.ShipsWarpingHere.Remove(ship);
        ship.Kill(null, true);
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
