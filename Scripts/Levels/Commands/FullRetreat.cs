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
    public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, WarpGate warpgate)
    {
        if (warpgate != null)
        {
            TargetWarpGate = warpgate;
            base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);

            
            // The ToList() is necessary to prevent errors from warp killing while looping through the list of ships
            GetSquad().GetShips().ToList().ForEach((ship) =>
            {
                if (ship.ShipType != ConfigData.ShipTypes.WarpGate)
                {
                    TargetWarpGate.ShipsWarpingHere.Add(ship.Id);
                    if (ship.Collider.IsTouching(TargetWarpGate.WarpCollider))
                    {
                        ShipsWaitingToWarp.Add(ship);
                    }
                }

            });
            if (TargetWarpGate.ShipsWarpingHere.Count > 0)
            {
                TargetWarpGate.ShipAnimation.SetActive(true);
                WaitToWarp();
                if (!IsDead)
                {
                    PrepareDamageToSendEntries(1);
                    MoveToWarpGate();
                    CommandTimer.Reuse(CommandFrequency, MoveToWarpGate, true);
                    Level.AddTimer(CommandTimer);
                    //InvokeRepeating(nameof(MoveToWarpGate), 0, CommandFrequency);
                }

            }
            else
            {
                SetFinalize("The only ships in this squad are Warp Gates");
            }


        }
        else
        {
            SetFinalize("The warp gate doesn't exist anymore, or there were no warp gates around");
        }

    }
    public override void ClearData()
    {
        base.ClearData();
        TargetWarpGate = null;
        ShipsWaitingToWarp.Clear();
    }

    Vector2 _f_targetPosition;
    public void MoveToWarpGate()
    {
        if (!GetSquad().IsDead)
        {
            if (!TargetWarpGate.IsDead)
            {
                _f_targetPosition = TargetWarpGate.GetPosition() + TargetWarpGate.WarpPoint;
                GetSquad().GetShips().ForEach((ship) =>
                {
                    if (ship.ShipType != ConfigData.ShipTypes.WarpGate)
                    {
                        ship.MoveToPoint(_f_targetPosition);
                    }
                });
                GetSquad().Status = $"Moving to {TargetWarpGate.Name} to warp out of the level: {_f_targetPosition}";
            }
            else
            {
                SetFinalize("Warp gate was destroyed");
            }
        }
        
    }

    private List<Ship> _tempShips;
    private ScaledTimer _waitToWarpTimer = new ScaledTimer();
    public void WaitToWarp()
    {
        if (TargetWarpGate.ShipAnimationController.IsReadyToWarp && TargetWarpGate.ShipsWarpingHere.Count > 0)
        {
            _tempShips = ShipsWaitingToWarp.ToList();
            _tempShips.ForEach((ship) =>
            {
                WarpKill(ship);
            });
            ShipsWaitingToWarp.Clear();
        }
        else
        {
            _waitToWarpTimer.Reuse(2, WaitToWarp);
            Level.AddTimer(_waitToWarpTimer);
            //Invoke(nameof(WaitToWarp), 2);
        }
    }

    public void WarpKill(Ship ship)
    {
        if (!IsDead)
        {
            // Commented this out because it seemed to give too high of a reward for Full Retreat. Now, it will always give 0 or less, which means it should be preferable to losing
            // but not much better than that
            //if (ship.ShipType != ConfigData.ShipTypes.Striker && ship.ShipType != ConfigData.ShipTypes.Drone)
            //{
            //    Tsv += (int)(ship.Tsv * .05f);
            //}
            TargetWarpGate.ShipsWarpingHere.Remove(ship.Id);
            ship.EndKill(); // if this is the last ship, this call could kill the command as well
            if (!IsDead && TargetWarpGate.ShipsWarpingHere.Count == 0)
            {
                SetFinalize("All ships have warped");
            }
        }

    }

    public void CleanupWarpGate()
    {
        //Debug.Log($"Clenaing up warp gate: {TargetWarpGate}");
        if (TargetWarpGate != null && !TargetWarpGate.IsDead)
        {
            if (!GetSquad().IsDead)
            {
                GetSquad().GetShips().ForEach((ship) =>
                {
                    TargetWarpGate.ShipsWarpingHere.Remove(ship.Id);
                });
            }
            else
            {
                TargetWarpGate.ShipsWarpingHere.RemoveWhere((s) =>
                {
                    return Level.State.GetShipById(s) == null;
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
        Level.CancelTimer(_waitToWarpTimer);
        CleanupWarpGate();
        base.SetFinalize(cause);
    }
}
