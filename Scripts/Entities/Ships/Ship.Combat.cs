using System;
using System.Collections.Generic;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Projectiles;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Levels.Commands;
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
        private Carrier _nextCarrier;
        private CarrierShip _carrierShip;
        private int _maxLoops;

        public void ClearTargets()
        {
            for (int i = 0; i < Weapons.Count; i++)
            {
                Weapons[i].ClearTargets();
            }
        }

        private void CombatTimer()
        {
            InCombat = false;
            Level.CancelTimer(_combatTimerScaledTimer);
            _combatTimer = false;
        }

        public void SetCombatTimer()
        {
            if (!Level.Stage.ActivateHiveMind) return;
            bool timerWasActive = _combatTimer && !_combatTimerScaledTimer.IsCanceled;
            InCombat = true;
            _combatTimer = true;
            _combatTimerScaledTimer.Reuse(_repeatRate, CombatTimer, true);
            if (!timerWasActive)
            {
                Level.AddTimer(_combatTimerScaledTimer);
            }
        }

        public void LogDamage(int damage)
        {
            if (Health <= 0) return;
            _oldTsv = Tsv;
            Health -= math.min(damage, Health);
            Tsv = Utilities.CalculateTsv(this);
            _tsvChange = Tsv - _oldTsv;
            FleetShip.DamageReceived += -_tsvChange;
            Squad.SavedSquad.Stats.DamageReceived += -_tsvChange;
            if (Squad.HasCommand) Squad.GetCommand().Tsv += _tsvChange;
            if (Health == 0) Kill(null, null, null);
            else UpdateHealthBar();
        }

        public static void LogAttackingDamage(int power, Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target, long attackerCommandOutcomeId = 0)
        {
            if (target.Health <= 0) return;
            if (target.Level.Stage.MakeShotsHarmless) power = 0;
            attacker.ShipsHit.Add(target);
            int appliedDamage = math.min(power, target.Health);
            _targetOldTSV = target.Tsv;
            target.Health -= appliedDamage;
            target.Tsv = Utilities.CalculateTsv(target);
            _targetTSVChange = target.Tsv - _targetOldTSV;

            // The exact combat TSV loss only exists after health and TSV have been recalculated.
            // Emit RL hit shaping here so it is credited at impact rather than at episode timeout.
            global::RlOneVsOneEpisodeCoordinator.RecordHit(attacker, target, appliedDamage, -_targetTSVChange);
            LogHitStats(attacker, attackerFleetShip, attackerSavedSquad, target, target.Squad, -_targetTSVChange, attackerCommandOutcomeId);

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

        private static void CreditShootingTsv(Ship ship, int tsvDelta, long commandOutcomeId = 0)
        {
            if (ship?.Level?.State == null)
            {
                return;
            }

            long shootingOutcomeOwner = commandOutcomeId;
            if (shootingOutcomeOwner <= 0)
            {
                Command activeCommand = ship.Squad?.GetCommand();
                shootingOutcomeOwner = activeCommand?.OutcomeId ?? 0;
            }

            if (shootingOutcomeOwner > 0)
            {
                ship.Level.State.AddShootingTsvToStoredCommand(shootingOutcomeOwner, tsvDelta);
            }
        }

        private static void CreditAttackerCommandTsv(Ship attacker, int tsvDelta, long attackerCommandOutcomeId)
        {
            if (attacker == null)
            {
                return;
            }

            Command activeCommand = attacker.Squad?.GetCommand();
            CreditShootingTsv(attacker, tsvDelta, attackerCommandOutcomeId);

            if (attackerCommandOutcomeId > 0)
            {
                if (activeCommand != null && activeCommand.OutcomeId == attackerCommandOutcomeId)
                {
                    activeCommand.Tsv += tsvDelta;
                    return;
                }

                // The projectile can outlive the command that fired it. PastCommands is
                // retained until the level flush, so delayed damage still belongs to that
                // originating outcome rather than whichever command is active at impact.
                if (!attacker.Level.State.AddTsvToStoredCommand(attackerCommandOutcomeId, tsvDelta))
                {
                    Debug.LogError($"Could not attribute delayed damage TSV to command outcome #{attackerCommandOutcomeId}.");
                }
                return;
            }

            // Synchronous/user damage has no stable projectile outcome to recover and keeps
            // the historical strategic behavior of crediting the command active at impact.
            if (activeCommand != null)
            {
                activeCommand.Tsv += tsvDelta;
            }
        }

        protected static void LogHitStats(Ship attacker, FleetShip attackerFleetShip, SavedSquad attackerSavedSquad, Ship target, Squad targetSquad, int tsvLoss, long attackerCommandOutcomeId = 0)
        {
            if (tsvLoss < 0) Debug.LogError($"The tsv loss for target {target.Name} is negative when it should be positive: {tsvLoss}");
            _isFriendlyFire = attackerFleetShip.Side == target.Side;
            if (!_isFriendlyFire)
            {
                attackerFleetShip.DamageDone += tsvLoss;
                attackerSavedSquad.Stats.DamageDone += tsvLoss;
            }
            else if (attacker.KillerFleetShip != null)
            {
                // If an enemy killed an explosive attacker (for example a Fire Barge),
                // preserve the historical chain-reaction credit for the external killer.
                // The attacking command itself is still penalized below for all same-side damage.
                attacker.KillerFleetShip.DamageDone += tsvLoss;
                attacker.KillerSavedSquad.Stats.DamageDone += tsvLoss;
                if (attacker.Killer != null)
                {
                    CreditAttackerCommandTsv(attacker.Killer, tsvLoss, attacker.KillerCommandOutcomeId);
                }
            }

            CreditAttackerCommandTsv(attacker, tsvLoss * (_isFriendlyFire ? -1 : 1), attackerCommandOutcomeId);

            if (target != null)
            {
                target.FleetShip.DamageReceived += tsvLoss;
                target.Squad.SavedSquad.Stats.DamageReceived += tsvLoss;
                if (targetSquad.HasCommand)
                {
                    targetSquad.GetCommand().Tsv -= tsvLoss;
                    CreditShootingTsv(target, -tsvLoss);
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
                FleetShip.IsDead = true;
            Squad.SavedSquad.Stats.ShipsLost++;
            FleetShip.MineralsMinedThisLevel = 0;
            if (Side == ConfigData.Configuration.UserSide)
            {
                // Campaign base objectives such as Pluto and Titania are represented by a
                // HumanTarget so the Bees can target them, but they are not player-owned fleet
                // assets. Their very large synthetic TSV must never become a player score/loss.
                if (ShipType != ConfigData.ShipTypes.HumanTarget)
                {
                    Level.State.PlayerScore -= FleetShip.GetTsv();
                    Level.State.RecordPlayerShipLost(ShipType);
                }
            }
            else Level.State.PlayerScore += FleetShip.GetTsv();
        }

        public void EndKill() => Kill(null, null, null, true);

        public void KilledShip(Ship victim)
        {
            LastKilled = Time.frameCount;
            for (int i = 0; i < Weapons.Count; i++)
            {
                Weapon weapon = Weapons[i];
                weapon.ShipsWithinRange.Remove(victim.Id);
                weapon.HasCachedChanged = true;
            }
        }

        /// <summary>
        /// Cancels all Level-owned timers and weapon timers associated with this ship's
        /// current lifecycle. Special ships that override Kill() must call this too.
        /// </summary>
        protected void CancelOwnedTimers()
        {
            Level.CancelTimer(_asteroidDoubleCheckTimer);
            Level.CancelTimer(_tryToFindPathAgainTimer);
            Level.CancelTimer(_combatTimerScaledTimer);
            Level.CancelTimer(_showShipStatsTimer);
            if (HasWeapons)
            {
                for (int i = 0; i < Weapons.Count; i++)
                {
                    Weapons[i].CancelTimer();
                }
            }
        }

        public virtual void Kill(Ship killer, FleetShip killerFleetShip, SavedSquad killerSavedSquad, bool endKill = false)
        {
            if (IsDead) return;
            IsDead = true;
            if (!endKill)
            {
                DropExplosionAnimation();
                if (killerFleetShip != null)
                {
                    if (killer != null)
                    {
                        killer.KilledShip(this);
                        if (killer.Side == ConfigData.Configuration.UserSide) Level.State.EnemyShipsDestroyedByPlayer++;
                    }
                    LogKillerStats(killerFleetShip, killerSavedSquad);
                }
                if (ShipType != ConfigData.ShipTypes.Beacon) LogKilledStats();
                if (HasUserFogOfWarVision) FogOfWarVision.Kill(0, false);
                if (WeaponsThatHaveUsWithinRange.Count > 0)
                {
                    foreach (Weapon weapon in WeaponsThatHaveUsWithinRange)
                    {
                        weapon.ShipsWithinRange.Remove(Id);
                    }
                    WeaponsThatHaveUsWithinRange.Clear();
                }
            }
            else if (Side == ConfigData.Configuration.UserSide) Level.State.PlayerShipsReturned++;

            Level.State.RemoveShip(this);
            Squad.RemoveShip(this);
            if (ShipType == ConfigData.ShipTypes.Carrier)
            {
                _nextCarrier = null;
                List<Ship> levelShips = Level.State.Ships;
                for (int i = 0; i < levelShips.Count; i++)
                {
                    Ship ship = levelShips[i];
                    if (ship.Side == Side && ship is Carrier carrier && !carrier.IsDead)
                    {
                        _nextCarrier = carrier;
                        break;
                    }
                }
                if (_nextCarrier != null)
                {
                    for (int i = 0; i < levelShips.Count; i++)
                    {
                        Ship ship = levelShips[i];
                        if (ship.Side != Side || !ship.Squad.IsCarrierSquad)
                        {
                            continue;
                        }
                        _carrierShip = (CarrierShip)ship;
                        if (_carrierShip.Carrier == this)
                        {
                            _carrierShip.Carrier = _nextCarrier;
                        }
                    }
                }
            }

            foreach (Projectile projectile in ProjectilesInFlight) projectile.ShipIsDead = true;
            if (Squad.GetShips().Count == 0)
            {
                Squad.Kill(endKill);
            }
            else
            {
                Squad.SetOffsets();
                if (!endKill)
                {
                    // Recalculate selection bounds only after the casualty has actually been
                    // removed and the surviving formation offsets have been refreshed.
                    Squad.HasMovedBox = false;
                    Squad.MoveSquadBox();
                }
            }
            CancelOwnedTimers();
            Deactivate();
        }

        public Ship SetAndGetTargetEnemy()
        {
            Command command = Squad?.GetCommand();
            if (command == null || command.EnemySquad == null || command.EnemySquad.IsDead)
            {
                TargetEnemyShipToFollow = null;
                return null;
            }

            _tempIndex = 0;
            _maxLoops = math.max(command.EnemySquad.GetShips().Count, 10);
            while (!HasTargetEnemyShipToFollow && _tempIndex < _maxLoops)
            {
                _tempIndex++;
                if (command.TargetingQueue.Count == 0)
                {
                    command.RebuildTargetingQueues();
                    if (command.TargetingQueue.Count == 0)
                    {
                        return null;
                    }
                }

                TargetEnemyShipToFollow = command.TargetingQueue.Dequeue();
                if (TargetEnemyShipToFollow == null || TargetEnemyShipToFollow.IsDead)
                {
                    TargetEnemyShipToFollow = null;
                    command.RebuildOriginalTargetingQueue();
                }
            }
            return TargetEnemyShipToFollow;
        }
    }
}
