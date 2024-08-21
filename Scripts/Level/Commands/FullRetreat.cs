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
            Vector2 targetPosition = TargetWarpGate.GetPosition();
            Squad.GetShips().ForEach((ship) =>
            {
                ship.MoveToPoint(targetPosition);
            });
            Squad.Status = $"Moving to {TargetWarpGate.Name} to warp out of the level: {targetPosition}";
        }
        else if (TargetWarpGate.IsDead)
        {
            SetFinalize("Warp gate was destroyed");
        }
    }

    public void Warp(Ship ship)
    {
        Tsv += (int) (ship.Tsv * .05f);
        StartCoroutine(DelayedKill(ship));
    }

    public IEnumerator DelayedKill(Ship ship)
    {
        yield return new WaitForSeconds(2);
        ship.Kill(null, true);
    }
}
