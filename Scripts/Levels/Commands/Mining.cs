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
        public List<long> MiningShips = new List<long>();
        /// <summary>
        /// Ships in squad that are currently mining asteroids
        /// </summary>
        public List<long> ShipsCurrentlyMining = new List<long>();

        public void Execute(ConfigData.ShootingStrategyTypes shootingStrategy, long commandOutcomeId, long shootingStrategyOutcomeId, MiningAsteroid asteroid)
        {
            if (asteroid != null) // Needs to be null check in case there were no asteroids
            {
                MiningShips = GetSquad().GetShips().Where((ship) => ship.IsMiningShip).Select((s) => s.Id).ToList();
                TargetAstroid = asteroid;
                base.Execute(shootingStrategy, commandOutcomeId, shootingStrategyOutcomeId, true);
                PrepareDamageToSendEntries(1);

                // Check if any ships are already on the asteroid
                GetSquad().GetShips().ForEach((ship) =>
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
                CommandTimer.Reuse(CommandFrequency, Timer, true, true);
                Level.AddTimer(CommandTimer);
                //InvokeRepeating(nameof(MoveToAsteroid), 0, CommandFrequency);
                if (IsHiveMindCommand)
                {
                    TimeoutTimer.Reuse(300, Timeout);
                    Level.AddTimer(TimeoutTimer);
                    //Invoke(nameof(EndCommand), 300); // 5 minutes
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
            _hasFoundAsteroid = false;
            MiningShips.Clear();
            ShipsCurrentlyMining.Clear();
        }

        private Vector2 _position;
        public void Timer()
        {
            if (!GetSquad().IsDead && !TargetAstroid.IsDead)
            {
                _position = TargetAstroid.GetPosition();
                SetAndMove(_position);
                GetSquad().Status = $"Moving to {TargetAstroid.Name} to start mining: {_position}";

            }
            else if (TargetAstroid.IsDead)
            {
                SetFinalize("Mining asteroid was destroyed");
            }
        }
        private ScaledTimer _miningTimer = new ScaledTimer();
        private ScaledTimer _stopMovingTowardsAsteroidTimer = new ScaledTimer();
        private bool _hasFoundAsteroid = false;
        public void FoundAsteroid(Ship ship)
        {
            if (!_hasFoundAsteroid)
            {
                _hasFoundAsteroid = true;
                ShipsCurrentlyMining.Add(ship.Id);
                if (ship.HasShipAnimation)
                {
                    ship.ShipAnimation.SetActive(true);
                }
                if (ShipsCurrentlyMining.Count == 1)
                {
                    _miningTimer.Reuse(3, Mine, true);
                    Level.AddTimer(_miningTimer);
                    //InvokeRepeating(nameof(Mine), 0, 3);
                }
                if (MiningShips.All((s) => Level.State.GetShipById(s) != null || ShipsCurrentlyMining.Contains(s)))
                {
                    _stopMovingTowardsAsteroidTimer.Reuse(5, StopMovingTowardsAsteroid);
                    Level.AddTimer(_stopMovingTowardsAsteroidTimer);
                    //Invoke(nameof(StopMovingTowardsAsteroid), 5);
                }
            }

        }

        public void StopMovingTowardsAsteroid()
        {
            Level.CancelTimer(CommandTimer);
            //CancelInvoke(nameof(MoveToAsteroid));
        }
        private int _miningRate, _amountMined, _amountPerShip;
        public void Mine() // [stats-method]
        {
            if (!TargetAstroid.IsDead)
            {
                ShipsCurrentlyMining = ShipsCurrentlyMining.Where((s) => Level.State.GetShipById(s) != null).ToList();
                if (ShipsCurrentlyMining.Count > 0)
                {
                    //Debug.Log($"There are {ShipsMining.Count} ships mining for {Squad.Name}");
                    _miningRate = ConfigData.MiningRate * ShipsCurrentlyMining.Count;
                    _amountMined = math.min(_miningRate, TargetAstroid.Health); // [TSV] The health of the mining asteroids should be adjusted if the TSV is adjusted

                    Tsv += _amountMined;
                    TargetAstroid.Health -= _amountMined;
                    //Debug.Log($"{GetSquad().Name} mined {_amountMined} from {TargetAstroid.Name}. It has {TargetAstroid.Health} health left");

                    _amountPerShip = _amountMined / ShipsCurrentlyMining.Count; // this isn't exactly the same as MiningRate because the shsip might have mined the last of the asteroid
                    ShipsCurrentlyMining.Select((s) => Level.State.GetShipById(s)).ToList().ForEach((ship) =>
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
                TargetAstroid.SquadsMining.Remove(GetSquad());
            }
        }
        public override void SetFinalize(string cause)
        {
            Level.CancelTimer(_miningTimer);
            CleanupAsteroid();
            //Debug.Log($"Finalizing mining command for {Squad}");
            GetSquad().GetShips().ForEach((ship) =>
            {
                if (ship.HasShipAnimation)
                {
                    //Debug.Log($"Turning off mining animation for {ship.Name}");
                    ship.ShipAnimation.SetActive(false);
                }
            });
            base.SetFinalize(cause);
        }
    }
}