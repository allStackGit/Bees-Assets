using Assets.Scripts;
using Assets.Scripts.Entities;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Runs the shared Bees RL policy in ordinary Stage scenes when the existing serialized
/// ActivateBrains switch is enabled. Unlike the dedicated self-play adapter, this controller does
/// not own episode resets or promotion state. It only replaces the Hive Mind's low-level control
/// surface; player-owned ships remain under player control.
/// </summary>
internal sealed class RlLivePolicyAgent : Agent
{
    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;
    private const float MiningActionIntervalSeconds = 5f;
    private const float HealingActionIntervalSeconds = 1f;
    private const int HealingPerSuccessfulAction = 50;

    private static readonly List<RlLivePolicyAgent> Instances = new List<RlLivePolicyAgent>();
    private static int _lastProvisionFrame = -1;

    private Stage _stage;
    private Level _level;
    private Ship _ship;
    private int _side;
    private int _decisionCounter;
    private bool _hasBoundShip;
    private long _boundRuntimeShipId;
    private float _nextMiningActionTime;
    private float _nextHealingActionTime;
    private readonly Vector2[] _weaponAimDirections = new Vector2[RlOneVsOneAgent.MaxWeaponSlots];
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly RlCombatPerception _perception = new RlCombatPerception();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForPlayerFacingStage()
    {
        if (RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (!IsLiveRlEnabled(stage))
        {
            return;
        }

        stage.StartCoroutine(InstallWhenStageIsReady(stage));
    }

    private static IEnumerator InstallWhenStageIsReady(Stage stage)
    {
        while (stage != null && (!stage.IsFinalized || ConfigData.Configuration == null))
        {
            yield return null;
        }

        if (!IsLiveRlEnabled(stage) || RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            yield break;
        }

        RlPolicySchema.ValidateOrThrow();
        _lastProvisionFrame = -1;
        ProvisionAgentsForSpawnedShips(stage, force: true);

        Debug.Log($"Live RL policy controller enabled: ABI v{RlPolicySchema.Version} " +
                  $"{RlPolicySchema.Signature}; behavior={RlOneVsOneAgent.BehaviorName}. " +
                  "Player-owned ships remain player controlled.");
    }

    internal static bool ShouldControlSide(bool levelHasPlayer, int side, int aiSide)
    {
        return !levelHasPlayer || side == aiSide;
    }

    private static bool IsLiveRlEnabled(Stage stage)
    {
        // ActivateHiveMind remains the long-standing campaign AI enable/disable gate. ActivateBrains
        // selects which controller owns that enabled AI: false = Hive Mind, true = shared RL policy.
        return stage != null && stage.ActivateHiveMind && stage.ActivateBrains;
    }

    private static void ProvisionAgentsForSpawnedShips(Stage stage, bool force = false)
    {
        if (!IsLiveRlEnabled(stage) || ConfigData.Configuration == null ||
            (!force && (Time.frameCount == _lastProvisionFrame ||
                        Time.frameCount % RlOneVsOneTrainingOptions.DefaultDecisionPeriod != 0)))
        {
            return;
        }

        _lastProvisionFrame = Time.frameCount;
        IReadOnlyList<Level> levels = stage.Levels;
        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            Level level = levels[levelIndex];
            if (level == null || level.State == null)
            {
                continue;
            }

            EnsureAgentCount(stage, level, ConfigData.Configuration.BeeSide, levelIndex);
            EnsureAgentCount(stage, level, ConfigData.Configuration.HumanSide, levelIndex);
        }
    }

    private static void EnsureAgentCount(Stage stage, Level level, int side, int levelIndex)
    {
        if (!ShouldControlSide(level.HasPlayer, side, ConfigData.Configuration.AISide))
        {
            return;
        }

        int required = 0;
        List<Ship> ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            if (RlOneVsOneAgent.RequiresPolicyControl(ships[i]))
            {
                required++;
            }
        }

        // Keep one dormant controller available for reinforcements/spawned children even when the
        // side has no controllable ship at the instant the Stage finishes setup.
        required = Mathf.Max(1, required);
        int existing = CountAgents(level, side);
        for (int slot = existing; slot < required; slot++)
        {
            CreateAgent(stage, level, side, $"Arena {levelIndex} Side {side} Slot {slot}");
        }
    }

    private static int CountAgents(Level level, int side)
    {
        int count = 0;
        for (int i = 0; i < Instances.Count; i++)
        {
            RlLivePolicyAgent agent = Instances[i];
            if (agent != null && agent._level == level && agent._side == side)
            {
                count++;
            }
        }
        return count;
    }

    private static void CreateAgent(Stage stage, Level level, int side, string label)
    {
        GameObject obj = new GameObject($"Live RL Combat Agent - {label}");
        obj.transform.SetParent(level != null ? level.transform : stage.transform, false);

        BehaviorParameters behavior = obj.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = RlOneVsOneAgent.BehaviorName;
        behavior.TeamId = 0;
        behavior.BrainParameters.VectorObservationSize = RlOneVsOneAgent.ObservationSize;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = new ActionSpec(
            RlOneVsOneAgent.ContinuousActionCount,
            RlOneVsOneAgent.CreateDiscreteBranchSizes());

        RlLivePolicyAgent agent = obj.AddComponent<RlLivePolicyAgent>();
        agent._stage = stage;
        agent._level = level;
        agent._side = side;
    }

    public override void Initialize()
    {
        Instances.Add(this);
        ResetWeaponAimDirections();
    }

    protected override void OnDisable()
    {
        Instances.Remove(this);
        ReleaseShip();
        base.OnDisable();
    }

    private void FixedUpdate()
    {
        if (!IsLiveRlEnabled(_stage))
        {
            ReleaseShip();
            _hasBoundShip = false;
            _boundRuntimeShipId = 0;
            return;
        }

        ProvisionAgentsForSpawnedShips(_stage);
        if (!TryBindShip())
        {
            return;
        }

        _decisionCounter++;
        if (_decisionCounter >= RlOneVsOneTrainingOptions.DefaultDecisionPeriod)
        {
            _decisionCounter = 0;
            RequestDecision();
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (!TryBindShip())
        {
            for (int i = 0; i < RlOneVsOneAgent.ObservationSize; i++)
            {
                sensor.AddObservation(0f);
            }
            return;
        }

        _perception.Collect(_ship, _side, sensor, 0);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        bool canControl = TryBindShip();
        for (int slot = 0; slot < RlOneVsOneAgent.MaxWeaponSlots; slot++)
        {
            bool enabled = canControl && HasTurretForSlot(slot);
            actionMask.SetActionEnabled(
                RlOneVsOneAgent.WeaponFireBranchStart + slot,
                RlOneVsOneAgent.FireWeaponAction,
                enabled);
        }

        actionMask.SetActionEnabled(RlOneVsOneAgent.SpecialActionBranch, RlOneVsOneAgent.ShipSpecialAction,
            canControl && HasSpecialAction(_ship));
        actionMask.SetActionEnabled(RlOneVsOneAgent.SpecialActionBranch, RlOneVsOneAgent.MiningAction,
            canControl && RlOneVsOneAgent.CanUseMiningAction(_ship));
        actionMask.SetActionEnabled(RlOneVsOneAgent.SpecialActionBranch, RlOneVsOneAgent.HealingAction,
            canControl && RlOneVsOneAgent.CanUseHealingAction(_ship));
        actionMask.SetActionEnabled(RlOneVsOneAgent.SpecialActionBranch, RlOneVsOneAgent.WarpAction,
            canControl && RlOneVsOneAgent.CanUseWarpAction(_ship));

        DisableTargetBranches(actionMask);
    }

    private static void DisableTargetBranches(IDiscreteActionMask actionMask)
    {
        for (int action = 1; action < RlOneVsOneAgent.AllyTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(RlOneVsOneAgent.AllyTargetBranch, action, false);
        }
        for (int action = 1; action < RlOneVsOneAgent.EnemyTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(RlOneVsOneAgent.EnemyTargetBranch, action, false);
        }
        for (int action = 1; action < RlOneVsOneAgent.MapObjectTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(RlOneVsOneAgent.MapObjectTargetBranch, action, false);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!TryBindShip())
        {
            return;
        }

        ActionSegment<float> continuous = actions.ContinuousActions;
        ApplyMovement(new Vector2(continuous[0], continuous[1]));

        ActionSegment<int> discrete = actions.DiscreteActions;
        for (int slot = 0; slot < RlOneVsOneAgent.MaxWeaponSlots; slot++)
        {
            int aimStart = RlOneVsOneAgent.WeaponAimContinuousActionStart +
                           slot * RlOneVsOneAgent.WeaponAimContinuousActionsPerSlot;
            Vector2 aim = new Vector2(continuous[aimStart], continuous[aimStart + 1]);
            if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone)
            {
                _weaponAimDirections[slot] = aim.normalized;
            }

            ApplyWeaponCommand(
                slot,
                _weaponAimDirections[slot],
                discrete[RlOneVsOneAgent.WeaponFireBranchStart + slot] == RlOneVsOneAgent.FireWeaponAction);
        }

        switch (discrete[RlOneVsOneAgent.SpecialActionBranch])
        {
            case RlOneVsOneAgent.ShipSpecialAction:
                ApplySpecialAction();
                break;
            case RlOneVsOneAgent.MiningAction:
                TryApplyMiningAction();
                break;
            case RlOneVsOneAgent.HealingAction:
                TryApplyHealingAction();
                break;
            case RlOneVsOneAgent.WarpAction:
                TryApplyWarpAction();
                break;
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // Player-facing builds must fail inert rather than issue random movement/fire if no trainer
        // or inference model is available for an explicitly enabled RL controller.
        ActionSegment<float> continuous = actionsOut.ContinuousActions;
        for (int i = 0; i < continuous.Length; i++)
        {
            continuous[i] = 0f;
        }
        ActionSegment<int> discrete = actionsOut.DiscreteActions;
        for (int i = 0; i < discrete.Length; i++)
        {
            discrete[i] = 0;
        }
    }

    private void ApplyMovement(Vector2 movement)
    {
        if (!_ship.IsMobile || _ship.CannotChangeMovementOrders)
        {
            _ship.HasBrain = true;
            return;
        }

        if (movement.sqrMagnitude < MovementDeadZone * MovementDeadZone)
        {
            _ship.Direction = 360;
        }
        else
        {
            Vector2 point = _ship.GetPosition() + movement.normalized;
            int direction = Mathf.RoundToInt(_ship.GetDegreesTowardsPoint(point));
            _ship.Direction = ((direction % 360) + 360) % 360;
        }
        _ship.HasBrain = true;
    }

    private void ApplyWeaponCommand(int slot, Vector2 aimDirection, bool fire)
    {
        if (_ship.Weapons == null || slot < 0 || slot >= RlOneVsOneAgent.MaxWeaponSlots || slot >= _ship.Weapons.Count ||
            !(_ship.Weapons[slot] is Turret turret))
        {
            return;
        }

        Vector2 target = turret.GetPosition() + aimDirection * Mathf.Max(1f, turret.Range);
        turret.SetRlControl(target, fire);
    }

    private bool HasTurretForSlot(int slot)
    {
        return _ship != null && _ship.Weapons != null && slot >= 0 &&
               slot < RlOneVsOneAgent.MaxWeaponSlots && slot < _ship.Weapons.Count &&
               _ship.Weapons[slot] is Turret;
    }

    private void ApplySpecialAction()
    {
        if (_ship is YellowJacket yellowJacket)
        {
            yellowJacket.TryToDetonate();
        }
        else if (_ship is Striker striker)
        {
            striker.TryToDropBombs();
        }
        else if (_ship is FireBarge fireBarge)
        {
            fireBarge.Detonate();
        }
        else if (_ship is Barge barge && !barge.HasStartedCharging && !barge.IsCharging)
        {
            barge.StartCoroutine(barge.ChargeForward(FindNearestVisibleEnemy()));
        }
        else if (_ship is Scout scout)
        {
            scout.DropBeacon();
        }
    }

    private void TryApplyMiningAction()
    {
        if (!RlOneVsOneAgent.CanUseMiningAction(_ship) || Time.time < _nextMiningActionTime ||
            _ship.Level == null || _ship.Level.State == null || _ship.Collider == null || _ship.FleetShip == null)
        {
            return;
        }

        MiningAsteroid asteroid = FindTouchingMiningAsteroid();
        if (asteroid == null)
        {
            return;
        }

        int amountMined = Mathf.Min(ConfigData.MiningRate, asteroid.Health);
        if (amountMined <= 0)
        {
            return;
        }

        _nextMiningActionTime = Time.time + MiningActionIntervalSeconds;
        asteroid.Health -= amountMined;
        _ship.FleetShip.MineralsMinedThisLevel += amountMined;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        if (asteroid.Health <= 0 && !asteroid.IsDead)
        {
            asteroid.Kill(false);
        }
    }

    private MiningAsteroid FindTouchingMiningAsteroid()
    {
        MiningAsteroid selected = null;
        foreach (MiningAsteroid asteroid in _ship.Level.State.MiningAsteroids)
        {
            if (asteroid == null || asteroid.IsDead || asteroid.Collider == null ||
                !_ship.Collider.IsTouching(asteroid.Collider))
            {
                continue;
            }
            if (selected == null || asteroid.Id < selected.Id)
            {
                selected = asteroid;
            }
        }
        return selected;
    }

    private void TryApplyHealingAction()
    {
        if (!RlOneVsOneAgent.CanUseHealingAction(_ship) || Time.time < _nextHealingActionTime ||
            _ship.Health >= _ship.MaxHealth || _ship.Level == null || _ship.Level.State == null ||
            _ship.Collider == null || _ship.FleetShip == null)
        {
            return;
        }

        Beehive beehive = FindTouchingBeehive();
        if (beehive == null)
        {
            return;
        }

        int amountHealed = Mathf.Min(HealingPerSuccessfulAction, _ship.MaxHealth - _ship.Health);
        if (amountHealed <= 0)
        {
            return;
        }

        _nextHealingActionTime = Time.time + HealingActionIntervalSeconds;
        _ship.Health += amountHealed;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        _ship.UpdateHealthBar();
        if (_ship.Level.HasPlayer)
        {
            beehive.SpawnHealingCross();
        }
    }

    private Beehive FindTouchingBeehive()
    {
        Beehive selected = null;
        List<Ship> allies = _ship.Level.State.GetShips(_ship.Side);
        for (int i = 0; i < allies.Count; i++)
        {
            if (!(allies[i] is Beehive beehive) || beehive.IsDead || beehive.HealCollider == null ||
                !beehive.HealCollider.IsTouching(_ship.Collider))
            {
                continue;
            }
            if (selected == null || beehive.Id < selected.Id)
            {
                selected = beehive;
            }
        }
        return selected;
    }

    private void TryApplyWarpAction()
    {
        if (!RlOneVsOneAgent.CanUseWarpAction(_ship) || _ship.Level == null || _ship.Level.State == null ||
            _ship.Collider == null)
        {
            return;
        }

        WarpGate warpGate = FindTouchingWarpGate();
        if (warpGate == null)
        {
            return;
        }

        if (warpGate.IsUserControlled && warpGate.EnteringWarpGateSound != null)
        {
            warpGate.EnteringWarpGateSound.Play();
        }
        _ship.EndKill();
    }

    private WarpGate FindTouchingWarpGate()
    {
        WarpGate selected = null;
        List<Ship> allies = _ship.Level.State.GetShips(_ship.Side);
        for (int i = 0; i < allies.Count; i++)
        {
            if (!(allies[i] is WarpGate warpGate) || warpGate.IsDead || warpGate.WarpCollider == null ||
                !warpGate.WarpCollider.IsTouching(_ship.Collider))
            {
                continue;
            }
            if (selected == null || warpGate.Id < selected.Id)
            {
                selected = warpGate;
            }
        }
        return selected;
    }

    private bool TryBindShip()
    {
        if (_level == null || _level.State == null || ConfigData.Configuration == null ||
            !ShouldControlSide(_level.HasPlayer, _side, ConfigData.Configuration.AISide))
        {
            ReleaseShip();
            return false;
        }

        if (_hasBoundShip)
        {
            if (_ship != null && !_ship.IsDead && _ship.Level == _level && _ship.Id == _boundRuntimeShipId)
            {
                return true;
            }
            ReleaseShip();
            _hasBoundShip = false;
            _boundRuntimeShipId = 0;
        }

        _bindCandidates.Clear();
        List<Ship> ships = _level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (RlOneVsOneAgent.RequiresPolicyControl(candidate) && !IsControlledByAnotherAgent(candidate))
            {
                _bindCandidates.Add(candidate);
            }
        }
        if (_bindCandidates.Count == 0)
        {
            return false;
        }

        _bindCandidates.Sort(CompareShipsForControl);
        _ship = _bindCandidates[0];
        if (!RlPolicySchema.TryValidateShip(_ship, out string schemaError))
        {
            Debug.LogError(schemaError);
            _ship = null;
            return false;
        }

        _boundRuntimeShipId = _ship.Id;
        _hasBoundShip = true;
        _decisionCounter = 0;
        _nextMiningActionTime = 0f;
        _nextHealingActionTime = 0f;
        ResetWeaponAimDirections();

        if (_ship.Squad != null)
        {
            _ship.Squad.IsUserControlled = false;
            _ship.Squad.IsHiveMindControlled = true;
            _ship.Squad.CanAcceptUserInput = false;
        }
        _ship.HasBrain = true;

        for (int i = 0; i < _ship.Turrets.Count; i++)
        {
            Turret turret = _ship.Turrets[i];
            turret.SetRlControl(turret.GetPosition() + Vector2.up * Mathf.Max(1f, turret.Range), false);
        }
        return true;
    }

    private bool IsControlledByAnotherAgent(Ship candidate)
    {
        for (int i = 0; i < Instances.Count; i++)
        {
            RlLivePolicyAgent other = Instances[i];
            if (other != null && other != this && other._level == _level && other._side == _side &&
                other._hasBoundShip && other._ship == candidate)
            {
                return true;
            }
        }
        return false;
    }

    private static int CompareShipsForControl(Ship left, Ship right)
    {
        long leftFleet = left != null && left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
        long rightFleet = right != null && right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
        int compare = leftFleet.CompareTo(rightFleet);
        if (compare != 0)
        {
            return compare;
        }
        long leftRuntime = left != null ? left.Id : long.MaxValue;
        long rightRuntime = right != null ? right.Id : long.MaxValue;
        return leftRuntime.CompareTo(rightRuntime);
    }

    private void ResetWeaponAimDirections()
    {
        for (int i = 0; i < _weaponAimDirections.Length; i++)
        {
            _weaponAimDirections[i] = Vector2.up;
        }
    }

    private void ReleaseShip()
    {
        if (_ship != null)
        {
            _ship.HasBrain = false;
            for (int i = 0; i < _ship.Turrets.Count; i++)
            {
                _ship.Turrets[i].ClearRlControl();
            }
        }
        _ship = null;
    }

    private Ship FindNearestVisibleEnemy()
    {
        if (_ship == null || _ship.Level == null || _ship.Level.State == null)
        {
            return null;
        }

        Ship selected = null;
        float selectedDistance = float.MaxValue;
        Vector2 origin = _ship.GetPosition();
        foreach (Ship candidate in _ship.Level.State.GetShipsVisibleToHiveMind(_side))
        {
            if (candidate == null || candidate.IsDead || candidate.Side == _side)
            {
                continue;
            }

            float distance = (candidate.GetPosition() - origin).sqrMagnitude;
            if (selected == null || distance < selectedDistance ||
                (Mathf.Approximately(distance, selectedDistance) && candidate.Id < selected.Id))
            {
                selected = candidate;
                selectedDistance = distance;
            }
        }
        return selected;
    }

    private static bool HasSpecialAction(Ship ship)
    {
        return ship is YellowJacket || ship is Striker || ship is FireBarge || ship is Barge || ship is Scout;
    }
}
