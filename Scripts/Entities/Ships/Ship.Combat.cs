using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships.Weapons;
using Unity.Mathematics;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private readonly ScaledTimer _combatTimerScaledTimer = new ScaledTimer();
        private float _maxRateOfFire, _repeatRate;
        private int _oldTsv, _tsvChange;
        private static int _targetOldTSV, _targetTSVChange;
        private static ShipDamageStatus _shipDamageStatus;
        private static bool _isFriendlyFire;
        private static int[] _initialTsv;
        private static float _percentageTsvDestroyed;
        private List<Weapon> _weapons;
        private Carrier _nextCarrier;
        private CarrierShip _carrierShip;
        private int _maxLoops;

        public void ClearTargets()
        {
            Weapons.ForEach(weapon => weapon.ClearTargets());
        }

        private void CombatTimer()
        {
            InCombat = false;
            Level.CancelTimer(_combatTimerScaledTimer);
            _combatTimer = false;
        }

        public void SetCombatTimer()
        {
            if (!IsUserControlled || !Level.Stage.ActivateHiveMind)
            {
                return;
            }
            if (_combatTimer)
            {
                Level.CancelTimer(_combatTimerScaledTimer);
            }
            InCombat = true;
            _combatTimer = true;
            _combatTimerScaledTimer.Reuse(_repeatRate, CombatTimer, true);
        }

        public void LogDamage(int damage)
        {
            if (Health <= 0)
            {
                return;
            }

            _oldTsv = Tsv;
            Health -= math.min(damage, Health);
            Tsv = Utilities.CalculateTsv(this);
            _tsvChange = Tsv - _oldTsv;
            FleetShip.DamageReceived += -_tsvChange;
            Squad.SavedSquad.Stats.DamageReceived += -_tsvChange;
            if (Squad.HasCommand)
            {
                Squad.GetCommand().Tsv += _tsvChange;
            }
            if (Health == 0) Kill(null, null, null);
            else UpdateHealthBar();
        }

        public static void LogAttackingDamage(int power, Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target)
        {
            if (target.Health <= 0)
            {
                return;
            }
            if (target.Level.Stage.MakeShotsHarmless)
            {
                power = 0;
            }

            attacker.ShipsHit.Add(target);
            _targetOldTSV = target.Tsv;
            target.Health -= math.min(power, target.Health);
            target.Tsv = Utilities.CalculateTsv(target);
            _targetTSVChange = target.Tsv - _targetOldTSV;
            LogHitStats(attacker, attackerFleetShip, attackerSavedSquad, target, target.Squad, -_targetTSVChange);

            if (target.Health == 0)
            {
                target.Kill(attacker, attackerFleetShip, attackerSavedSquad);
                if (attacker != null)
                {
                    attacker.Level.State.ShipDamageStatuses[attacker.Side - 1]
                        .Remove(attacker.Level.State.GetShipDamageStatus(attacker.Side, target));
                }
                return;
            }

            if (target.Level.Stage.IsTrainingNueralNetwork)
            {
                target.RLHealth = target.MaxHealth > 0 ? (float)target.Health / target.MaxHealth : 0f;
            }
            target.UpdateHealthBar();
            if (attacker != null)
            {
                _shipDamageStatus = target.Level.State.GetShipDamageStatus(attacker.Side, target);
                _shipDamageStatus.Health = target.Health;
            }
        }

        protected static void LogHitStats(Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target, Squad targetSquad, int tsvLoss)
        {
            if (tsvLoss < 0)
            {
                Debug.LogError($"The tsv loss for target {target.Name} is negative when it should be positive: {tsvLoss}");
            }

            _isFriendlyFire = false;
            if (attackerFleetShip.Side != target.Side)
            {
                attackerFleetShip.DamageDone += tsvLoss;
                attackerSavedSquad.Stats.DamageDone += tsvLoss;
            }
            else if (attacker.KillerFleetShip != null)
            {
                _isFriendlyFire = true;
                attacker.KillerFleetShip.DamageDone += tsvLoss;
                attacker.KillerSavedSquad.Stats.DamageDone += tsvLoss;
                if (attacker.Killer != null && attacker.Killer.Squad.HasCommand)
                {
                    attacker.Killer.Squad.GetCommand().Tsv += tsvLoss;
                }
            }

            if (attacker != null && attacker.Squad.HasCommand)
            {
                attacker.Squad.GetCommand().Tsv += tsvLoss * (_isFriendlyFire ? -1 : 1);
            }

            if (target != null)
            {
                target.FleetShip.DamageReceived += tsvLoss;
                target.Squad.SavedSquad.Stats.DamageReceived += tsvLoss;
                if (targetSquad.HasCommand)
                {
                    targetSquad.GetCommand().Tsv -= tsvLoss;
                }
                if (target.Stage.IsTrainingNueralNetwork)
                {
                    _initialTsv = target.Level.State.InitialTsv;
                    _percentageTsvDestroyed = _initialTsv[target.Side - 1] == 0
                        ? 0
                        : (float)Math.Round((double)tsvLoss / _initialTsv[target.Side - 1], 3);
                }
            }
            else if (targetSquad != null)
            {
                targetSquad.SavedSquad.Stats.DamageReceived += tsvLoss;
            }
        }

        protected void LogKillerStats(FleetShip killerFleetShip, SavedSquad killerSavedSquad)
        {
            killerFleetShip.Kills++;
            killerSavedSquad.Stats.Kills++;
        }

        protected void LogKilledStats()
        {
            if (Level.Stage.ReplaceDeadShips && !IsCarrierShip && !IsMinionShip && Squad.SavedSquad.HasBeenSavedToStorage)
            {
                FleetShip.IsDead = true;
            }
            Squad.SavedSquad.Stats.ShipsLost++;
            FleetShip.MineralsMinedThisLevel = 0;
            if (Side == ConfigData.Configuration.UserSide)
            {
                Level.State.PlayerScore -= FleetShip.GetTsv();
                Level.State.PlayerShipsLost++;
            }
            else
            {
                Level.State.PlayerScore += FleetShip.GetTsv();
            }
        }

        public void EndKill()
        {
            Kill(null, null, null, true);
        }

        public void KilledShip(Ship victim)
        {
            LastKilled = Time.frameCount;
            Weapons.ForEach(weapon =>
            {
                weapon.ShipsWithinRange.Remove(victim.Id);
                weapon.HasCachedChanged = true;
            });
        }

        public virtual void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (IsDead)
            {
                return;
            }
            IsDead = true;
            if (!endKill)
            {
                DropExplosionAnimation();
                if (killerFleetShip != null)
                {
                    if (killer != null)
                    {
                        killer.KilledShip(this);
                        if (killer.Side == ConfigData.Configuration.UserSide)
                        {
                            Level.State.EnemyShipsDestroyedByPlayer++;
                        }
                    }
                    LogKillerStats(killerFleetShip, killerSavedSquad);
                }
                if (ShipType != ConfigData.ShipTypes.Beacon)
                {
                    LogKilledStats();
                }
                if (HasUserFogOfWarVision)
                {
                    FogOfWarVision.Kill(0, false);
                }
                if (WeaponsThatHaveUsWithinRange.Count > 0)
                {
                    _weapons = WeaponsThatHaveUsWithinRange.ToList();
                    foreach (Weapon weapon in _weapons)
                    {
                        weapon.ShipsWithinRange.Remove(Id);
                    }
                    WeaponsThatHaveUsWithinRange.Clear();
                }
                Squad.HasMovedBox = false;
                Squad.MoveSquadBox();
            }
            else if (Side == ConfigData.Configuration.UserSide)
            {
                Level.State.PlayerShipsReturned++;
            }

            Level.State.RemoveShip(this);
            Squad.RemoveShip(this);

            if (ShipType == ConfigData.ShipTypes.Carrier)
            {
                _nextCarrier = (Carrier)Level.State.GetHumanShips().FirstOrDefault(ship => ship.ShipType == ConfigData.ShipTypes.Carrier);
                if (_nextCarrier != null)
                {
                    foreach (Ship ship in Level.State.GetHumanShips())
                    {
                        if (!ship.Squad.IsCarrierSquad) continue;
                        _carrierShip = (CarrierShip)ship;
                        if (_carrierShip.Carrier == this) _carrierShip.Carrier = _nextCarrier;
                    }
                }
            }

            foreach (var projectile in ProjectilesInFlight)
            {
                projectile.ShipIsDead = true;
            }

            if (Squad.GetShips().Count == 0) Squad.Kill(endKill);
            else Squad.SetOffsets();

            Level.CancelTimer(_asteroidDoubleCheckTimer);
            Level.CancelTimer(_combatTimerScaledTimer);
            if (HasWeapons) Weapons.ForEach(weapon => weapon.CancelTimer());
            Deactivate();
        }

        public Ship SetAndGetTargetEnemy()
        {
            _tempIndex = 0;
            _maxLoops = math.max(Squad.GetCommand().EnemySquad.GetShips().Count, 10);
            while (!HasTargetEnemyShipToFollow && _tempIndex < _maxLoops)
            {
                _tempIndex++;
                if (Squad.GetCommand().TargetingQueue.Count == 0)
                {
                    if (Squad.GetCommand().EnemySquad.IsGrowingSquad)
                    {
                        Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                    }
                    Squad.GetCommand().TargetingQueue = new Queue<Ship>(Squad.GetCommand().OriginalQueue);
                }
                TargetEnemyShipToFollow = Squad.GetCommand().TargetingQueue.Dequeue();
                if (TargetEnemyShipToFollow.IsDead)
                {
                    Squad.GetCommand().OriginalQueue = new Queue<Ship>(Squad.GetCommand().MakeTargetingQueue());
                }
            }
            if (_tempIndex == _maxLoops)
            {
                Debug.LogException(new Exception("Hit loop limit for SetAndGetTargetEnemy"));
            }
            return TargetEnemyShipToFollow;
        }
    }
}
