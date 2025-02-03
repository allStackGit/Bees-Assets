using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Levels.Commands
{
    /// <summary>
    /// Sends the ships to the selected asteroid to start mining 
    /// </summary>
    public class Mining : Command
    {
        public MiningAsteroid TargetAstroid;
        public List<Ship> MiningShips;
        public List<Ship> ShipsMining = new List<Ship>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy, MiningAsteroid asteroid)
        {
            if (asteroid != null)
            {
                MiningShips = Squad.GetShips().Where((ship) => ship.IsMiningShip).ToList();
                TargetAstroid = asteroid;
                base.Execute(ConfigData.CommandTypes.Mining, shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);
                PrepareDamageToSendEntries("closest");

                // Check if any ships are already on the asteroid
                Squad.GetShips().ForEach((ship) =>
                {
                    if (ship.Collider.IsTouching(TargetAstroid.Collider))
                    {
                        if (!TargetAstroid.SquadsMining.Contains(ship.Squad))
                        {
                            TargetAstroid.SquadsMining.Add(ship.Squad);

                        }
                        FoundAsteroid(ship);
                    }
                });
                InvokeRepeating(nameof(MoveToAsteroid), 0, CommandFrequency);
                if (IsHiveMindCommand)
                {
                    Invoke(nameof(EndCommand), 300); // 5 minutes
                }
            }
            else
            {
                SetFinalize("The asteroid doesn't exist anymore, or there were no asteroids around");
            }

        }
        public override void ClearData()
        {
            base.ClearData();
            TargetAstroid = null;
            MiningShips.Clear();
            ShipsMining.Clear();
        }

        public void MoveToAsteroid()
        {
            if (!Squad.IsDead && !TargetAstroid.IsDead)
            {
                Vector2 targetPosition = TargetAstroid.GetPosition();
                SetAndMove(targetPosition);
                Squad.Status = $"Moving to {TargetAstroid.Name} to start mining: {targetPosition}";

            }
            else if (TargetAstroid.IsDead)
            {
                SetFinalize("Mining asteroid was destroyed");
            }
        }
        public void FoundAsteroid(Ship ship)
        {
            ShipsMining.Add(ship);
            if (ship.HasShipAnimation)
            {
                ship.ShipAnimation.SetActive(true);
            }
            if (ShipsMining.Count == 1)
            {
                InvokeRepeating(nameof(Mine), 0, 3);
            }
            if (MiningShips.All((s) => s == null || s.IsDead || ShipsMining.Contains(s)))
            {
                Invoke(nameof(StopMovingTowardsAsteroid), 5);
            }
        }

        public void StopMovingTowardsAsteroid()
        {
            CancelInvoke(nameof(MoveToAsteroid));
        }

        public void Mine() // [stats-method]
        {
            ShipsMining = ShipsMining.Where((s) => s != null && !s.IsDead).ToList();
            if (ShipsMining.Count > 0)
            {
                //Debug.Log($"There are {ShipsMining.Count} ships mining for {Squad.Name}");
                int miningRate = ConfigData.MiningRate * ShipsMining.Count;
                int amountMined = math.min(miningRate, TargetAstroid.Health);

                Tsv += amountMined;
                TargetAstroid.Health -= amountMined;
                //Debug.Log($"{Squad.Name} mined {amountMined} from {TargetAstroid.Name}. It has {TargetAstroid.Health} health left");

                int amountPerShip = amountMined / ShipsMining.Count;
                ShipsMining.ForEach((ship) =>
                {
                    ship.FleetShip.MineralsMinedThisLevel += amountPerShip;
                    ship.Tsv += amountPerShip;
                });

                if (TargetAstroid.Health == 0)
                {
                    TargetAstroid.Kill();
                }
            }

        }
        public void CleanupAsteroid()
        {
            if (TargetAstroid != null && !TargetAstroid.IsDead)
            {
                TargetAstroid.SquadsMining.Remove(Squad);
            }
        }
        public override void SetFinalize(string cause)
        {
            CleanupAsteroid();
            //Debug.Log($"Finalizing mining command for {Squad}");
            Squad.GetShips().ForEach((ship) =>
            {
                if (ship.HasShipAnimation)
                {
                    //Debug.Log($"Turning off mining animation for {ship.Name}");
                    ship.ShipAnimation.SetActive(false);
                }
            });
            base.SetFinalize(cause);
        }
        public void EndCommand()
        {
            SetFinalize("Ran of out of time while mining");
        }
    }
}