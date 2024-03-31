using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Level.Commands
{
    /// <summary>
    /// Sends the ships to the selected asteroid to start mining 
    /// </summary>
    public class Mining : Command
    {
        public MiningAsteroid TargetAstroid;
        public int MiningRate = 750;
        public List<Ship> MiningShips;
        public List<Ship> ShipsMining = new List<Ship>();
        public void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy, MiningAsteroid asteroid)
        {
            if (asteroid != null)
            {
                MiningShips = Squad.GetShips().Where((ship) => ship.IsMiningShip).ToList();
                TargetAstroid = asteroid;
                base.Execute(strategy, shootingStrategy, commandOutcomeId, noEnemy);
                PrepareDamageToSendEntries("closest");
                InvokeRepeating(nameof(MoveToAsteroid), .1f, 1);
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
            if (ShipsMining.Count == 1)
            {
                InvokeRepeating(nameof(Mine), .1f, 3);
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
            //Debug.Log($"There are {ShipsMining.Count} ships mining for {Squad.Name}");
            int miningRate = MiningRate * ShipsMining.Count;
            int amountMined = miningRate;
            if (TargetAstroid.Health < miningRate)
            {
                amountMined = TargetAstroid.Health;
            }

            Tsv += amountMined;
            TargetAstroid.Health -= amountMined;
            //Debug.Log($"{Squad.Name} mined {amountMined} from {TargetAstroid.Name}. It has {TargetAstroid.Health} health left");
            if (TargetAstroid.Health <= 0)
            {
                TargetAstroid.Kill();
            }

            int amountPerShip = miningRate / ShipsMining.Count;
            ShipsMining.ForEach((ship) =>
            {
                ship.FleetShip.MineralsMinedThisLevel += amountPerShip;
                ship.AdditionalTsv += amountPerShip;
            });
        }
        public override void SetFinalize(string cause)
        {
            if (TargetAstroid != null && !TargetAstroid.IsDead)
            {
                TargetAstroid.SquadsMining.Remove(Squad);
            }
            base.SetFinalize(cause);
        }
        public void EndCommand()
        {
            SetFinalize("Ran of out of time while mining");
        }
    }
}