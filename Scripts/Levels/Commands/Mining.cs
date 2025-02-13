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
        /// <summary>
        /// Ships in the squad that can mine asteroids
        /// </summary>
        public List<Ship> MiningShips = new List<Ship>();
        /// <summary>
        /// Ships in squad that are currently mining asteroids
        /// </summary>
        public List<Ship> ShipsCurrentlyMining = new List<Ship>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, bool noEnemy, MiningAsteroid asteroid)
        {
            if (asteroid != null) // Needs to be null check in case there were no asteroids
            {
                MiningShips = Squad.GetShips().Where((ship) => ship.IsMiningShip).ToList();
                TargetAstroid = asteroid;
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, noEnemy);
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
            ShipsCurrentlyMining.Clear();
        }

        private Vector2 _position;
        public void MoveToAsteroid()
        {
            if (!Squad.IsDead && !TargetAstroid.IsDead)
            {
                _position = TargetAstroid.GetPosition();
                SetAndMove(_position);
                Squad.Status = $"Moving to {TargetAstroid.Name} to start mining: {_position}";

            }
            else if (TargetAstroid.IsDead)
            {
                SetFinalize("Mining asteroid was destroyed");
            }
        }
        public void FoundAsteroid(Ship ship)
        {
            ShipsCurrentlyMining.Add(ship);
            if (ship.HasShipAnimation)
            {
                ship.ShipAnimation.SetActive(true);
            }
            if (ShipsCurrentlyMining.Count == 1)
            {
                InvokeRepeating(nameof(Mine), 0, 3);
            }
            if (MiningShips.All((s) => s.IsDead || ShipsCurrentlyMining.Contains(s)))
            {
                Invoke(nameof(StopMovingTowardsAsteroid), 5);
            }
        }

        public void StopMovingTowardsAsteroid()
        {
            CancelInvoke(nameof(MoveToAsteroid));
        }
        private int _miningRate, _amountMined, _amountPerShip;
        public void Mine() // [stats-method]
        {
            if (!TargetAstroid.IsDead)
            {
                ShipsCurrentlyMining = ShipsCurrentlyMining.Where((s) => !s.IsDead).ToList();
                if (ShipsCurrentlyMining.Count > 0)
                {
                    //Debug.Log($"There are {ShipsMining.Count} ships mining for {Squad.Name}");
                    _miningRate = ConfigData.MiningRate * ShipsCurrentlyMining.Count;
                    _amountMined = math.min(_miningRate, TargetAstroid.Health);

                    Tsv += _amountMined;
                    TargetAstroid.Health -= _amountMined;
                    //Debug.Log($"{Squad.Name} mined {amountMined} from {TargetAstroid.Name}. It has {TargetAstroid.Health} health left");

                    _amountPerShip = _amountMined / ShipsCurrentlyMining.Count;
                    ShipsCurrentlyMining.ForEach((ship) =>
                    {
                        ship.FleetShip.MineralsMinedThisLevel += _amountPerShip;
                        ship.Tsv += _amountPerShip;
                    });

                    if (TargetAstroid.Health == 0)
                    {
                        TargetAstroid.Kill(false);
                    }
                }
                else
                {
                    SetFinalize("Asteroid has died");
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