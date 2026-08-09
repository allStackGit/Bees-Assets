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
    private HashSet<long> _shipIdsWarping = new HashSet<long>();

    public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, WarpGate warpgate)
    {
        if (warpgate != null)
        {
            TargetWarpGate = warpgate;

            // The ToList() is necessary to prevent errors from warp killing while looping through the list of ships.
            GetSquad().GetShips().ToList().ForEach((ship) =>
            {
                if (ship.ShipType != ConfigData.ShipTypes.WarpGate)
                {
                    _shipIdsWarping.Add(ship.Id);
                    TargetWarpGate.ShipsWarpingHere.Add(ship.Id);
                    if (ship.Collider.IsTouching(TargetWarpGate.WarpCollider))
                    {
                        QueueShipForWarp(ship);
                    }
                }
            });
            if (_shipIdsWarping.Count > 0)
            {
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                TargetWarpGate.ShipAnimationController.Activate();
                WaitToWarp();
                if (!IsDead)
                {
                    PrepareDamageToSendEntries(1);
                    MoveToWarpGate();
                    if (!IsDead)
                    {
                        CommandTimer.Reuse(CommandFrequency, MoveToWarpGate, true);
                        Level.AddTimer(CommandTimer);
                    }
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
        _shipIdsWarping.Clear();
        _isWaitingToWarp = false;
    }

    public void QueueShipForWarp(Ship ship)
    {
        if (ship != null && !ship.IsDead && _shipIdsWarping.Contains(ship.Id) && !ShipsWaitingToWarp.Contains(ship))
        {
            ShipsWaitingToWarp.Add(ship);
        }
        WaitToWarp();
    }

    private void RemoveUnavailableWarpParticipants()
    {
        foreach (long shipId in _shipIdsWarping.ToList())
        {
            Ship ship = Level.State.GetShipById(shipId);
            if (ship == null || ship.IsDead)
            {
                _shipIdsWarping.Remove(shipId);
                if (TargetWarpGate != null)
                {
                    TargetWarpGate.ShipsWarpingHere.Remove(shipId);
                }
            }
        }

        ShipsWaitingToWarp.RemoveAll((ship) => ship == null || ship.IsDead || !_shipIdsWarping.Contains(ship.Id));
    }

    Vector2 _f_targetPosition;
    public void MoveToWarpGate()
    {
        if (!GetSquad().IsDead)
        {
            if (!TargetWarpGate.IsDead)
            {
                RemoveUnavailableWarpParticipants();
                if (_shipIdsWarping.Count == 0)
                {
                    SetFinalize("No ships remain to warp");
                    return;
                }

                _f_targetPosition = TargetWarpGate.GetPosition() + Utilities.RandomInt(6) * Vector2.one;
                GetSquad().GetShips().ForEach((ship) =>
                {
                    if (_shipIdsWarping.Contains(ship.Id))
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
    private bool _isWaitingToWarp = false;
    public void WaitToWarp()
    {
        RemoveUnavailableWarpParticipants();
        if (_shipIdsWarping.Count == 0)
        {
            if (!IsDead)
            {
                SetFinalize("No ships remain to warp");
            }
            return;
        }

        if (TargetWarpGate.ShipAnimationController.IsReadyToWarp && ShipsWaitingToWarp.Count > 0)
        {
            _isWaitingToWarp = false;
            Level.CancelTimer(_waitToWarpTimer);
            _tempShips = ShipsWaitingToWarp.ToList();
            ShipsWaitingToWarp.Clear();
            _tempShips.ForEach((ship) =>
            {
                WarpKill(ship);
            });
        }
        else if (!_isWaitingToWarp)
        {
            _isWaitingToWarp = true;
            _waitToWarpTimer.Reuse(2, WaitToWarp, true);
            Level.AddTimer(_waitToWarpTimer);
        }
    }

    public void WarpKill(Ship ship)
    {
        if (IsDead || ship == null || !_shipIdsWarping.Contains(ship.Id))
        {
            return;
        }

        _shipIdsWarping.Remove(ship.Id);
        TargetWarpGate.ShipsWarpingHere.Remove(ship.Id);
        ShipsWaitingToWarp.Remove(ship);

        if (!ship.IsDead)
        {
            // Strikers and drones count as a loss of TSV since nothing is saved by killing them and its not better than killing them in combat, and almost certainly worse.
            if (ship.ShipType == ConfigData.ShipTypes.Striker || ship.ShipType == ConfigData.ShipTypes.Drone)
            {
                Tsv -= ship.Tsv;
            }
            if (TargetWarpGate.IsUserControlled)
            {
                TargetWarpGate.EnteringWarpGateSound.Play();
            }
            ship.EndKill(); // if this is the last ship, this call could kill the command as well
        }

        if (!IsDead && _shipIdsWarping.Count == 0)
        {
            SetFinalize("All ships have warped or become unavailable");
        }
    }

    public void CleanupWarpGate()
    {
        if (TargetWarpGate != null && !TargetWarpGate.IsDead)
        {
            foreach (long shipId in _shipIdsWarping.ToList())
            {
                TargetWarpGate.ShipsWarpingHere.Remove(shipId);
            }
            _shipIdsWarping.Clear();
            ShipsWaitingToWarp.Clear();

            if (TargetWarpGate.ShipsWarpingHere.Count == 0)
            {
                TargetWarpGate.ShipAnimationController.Deactivate();
                TargetWarpGate.ShipAnimationController.UseSecondaryLoop = false;
                TargetWarpGate.ShipAnimationController.IsReadyToWarp = false;
                TargetWarpGate.ShipAnimationController.SpriteIndex = 0;
            }
        }
    }

    public override void SetFinalize(string cause)
    {
        Level.CancelTimer(_waitToWarpTimer);
        CleanupWarpGate();
        base.SetFinalize(cause);
    }
}
