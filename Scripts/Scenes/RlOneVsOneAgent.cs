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
/// Fixed-shape shared combat policy adapter. Curriculum changes fill or clear reserved observation
/// and action slots instead of changing the neural-network contract.
/// </summary>
internal sealed class RlOneVsOneAgent : Agent
{
    internal const string BehaviorName = "BeesRL1v1";
    internal const int DecisionPeriod = 5;

    internal const int ShipTypeBitCount = RlCombatPerception.ShipTypeBitCount;
    internal const int WeaponTypeBitCount = RlCombatPerception.WeaponTypeBitCount;
    internal const int MapObjectTypeBitCount = RlCombatPerception.MapObjectTypeBitCount;
    internal const int MaxObservedAllies = RlCombatPerception.MaxObservedAllies;
    internal const int MaxObservedEnemies = RlCombatPerception.MaxObservedEnemies;
    internal const int MaxObservedMiningAsteroids = RlCombatPerception.MaxObservedMiningAsteroids;
    internal const int MaxObservedMapObjects = RlCombatPerception.MaxObservedMapObjects;
    internal const int MaxObservedCollisionAsteroids = RlCombatPerception.MaxObservedCollisionAsteroids;
    internal const int MaxObservedEnemyWeaponMounts = RlCombatPerception.MaxObservedEnemyWeaponMounts;
    internal const int NavigationGridSize = RlCombatPerception.NavigationGridSize;
    internal const int NavigationGridCellCount = RlCombatPerception.NavigationGridCellCount;
    internal const int MaxWeaponSlots = RlCombatPerception.MaxWeaponSlots;
    internal const int SelfObservationSize = RlCombatPerception.SelfObservationSize;
    internal const int CapabilityObservationSize = RlCombatPerception.CapabilityObservationSize;
    internal const int ParentCarrierObservationSize = RlCombatPerception.ParentCarrierObservationSize;
    internal const int EntityObservationSize = RlCombatPerception.EntityObservationSize;
    internal const int WeaponObservationSize = RlCombatPerception.WeaponObservationSize;
    internal const int EnemyWeaponMountObservationSize = RlCombatPerception.EnemyWeaponMountObservationSize;
    internal const int MiningAsteroidObservationSize = RlCombatPerception.MiningAsteroidObservationSize;
    internal const int MapObjectObservationSize = RlCombatPerception.MapObjectObservationSize;
    internal const int CollisionAsteroidObservationSize = RlCombatPerception.CollisionAsteroidObservationSize;
    internal const int ObjectiveObservationSize = RlCombatPerception.ObjectiveObservationSize;
    internal const int ObservationSize = RlCombatPerception.ObservationSize;

    // Movement occupies the first two continuous actions. Every authored weapon slot then gets its
    // own aim x/y pair and its own fire branch so all turrets can be aimed/fired independently in
    // the same policy decision. The capability branch remains primitive: it acts at CURRENT location.
    internal const int MovementContinuousActionCount = 2;
    internal const int WeaponAimContinuousActionsPerSlot = 2;
    internal const int WeaponAimContinuousActionStart = MovementContinuousActionCount;
    internal const int ContinuousActionCount = MovementContinuousActionCount + MaxWeaponSlots * WeaponAimContinuousActionsPerSlot;
    internal const int WeaponFireBranchStart = 0;
    internal const int WeaponFireBranchCount = MaxWeaponSlots;
    internal const int WeaponFireBranchSize = 2;
    internal const int CeaseWeaponAction = 0;
    internal const int FireWeaponAction = 1;
    internal const int SpecialActionBranch = WeaponFireBranchStart + WeaponFireBranchCount;
    internal const int AllyTargetBranch = SpecialActionBranch + 1;
    internal const int EnemyTargetBranch = AllyTargetBranch + 1;
    internal const int MapObjectTargetBranch = EnemyTargetBranch + 1;
    internal const int DiscreteBranchCount = MapObjectTargetBranch + 1;
    internal const int NoSpecialAction = 0;
    internal const int ShipSpecialAction = 1;
    internal const int MiningAction = 2;
    internal const int HealingAction = 3;
    internal const int WarpAction = 4;
    internal const int SpecialActionBranchSize = 5;
    internal const int AllyTargetBranchSize = 1 + MaxObservedAllies;
    internal const int EnemyTargetBranchSize = 1 + MaxObservedEnemies;
    internal const int MapObjectTargetBranchSize = 1 + MaxObservedMapObjects;

    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;
    private const float MiningActionIntervalSeconds = 5f;
    private const float HealingActionIntervalSeconds = 1f;
    private const int HealingPerSuccessfulAction = 50;

    private static readonly List<RlOneVsOneAgent> Instances = new List<RlOneVsOneAgent>();
    private static readonly Dictionary<int, int> AgentCounts = new Dictionary<int, int>();
    private static bool _invalidEnvironmentReported;
    private static int _lastProvisionFrame = -1;

    private Stage _stage;
    private Ship _ship;
    private int _side;
    private int _teamId;
    private int _decisionCounter;
    private int _lastRewardedEpisode;
    private bool _hasBoundShip;
    private bool _hasParticipatedThisEpisode;
    private long _boundRuntimeShipId;
    private float _nextMiningActionTime;
    private float _nextHealingActionTime;
    private readonly Vector2[] _weaponAimDirections = new Vector2[MaxWeaponSlots];
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly RlCombatPerception _perception = new RlCombatPerception();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForDedicatedTrainingScene()
    {
        if (!RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            return;
        }

        Stage stage = Object.FindFirstObjectByType<Stage>();
        if (stage == null)
        {
            Debug.LogError("RL combat policy adapter could not find the training Stage.");
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

        if (stage == null || !RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime)
        {
            yield break;
        }
        if (stage.GetComponentsInChildren<RlOneVsOneAgent>(true).Length > 0)
        {
            yield break;
        }

        RlPolicySchema.ValidateOrThrow();
        AgentCounts.Clear();
        _lastProvisionFrame = -1;
        _invalidEnvironmentReported = false;

        int initialSlots = RlOneVsOneTrainingBootstrap.CurrentShipsPerSide;
        for (int slot = 0; slot < initialSlots; slot++)
        {
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 0, $"Bee Team 0 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 1, $"Bee Team 1 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 0, $"Human Team 0 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 1, $"Human Team 1 Slot {slot}");
        }

        Debug.Log($"RL policy ABI v{RlPolicySchema.Version} {RlPolicySchema.Signature} " +
                  $"observations={ObservationSize} continuous_actions={ContinuousActionCount} " +
                  $"weapon_fire_branches={WeaponFireBranchCount}x{WeaponFireBranchSize} " +
                  $"special_branch={SpecialActionBranchSize} ally_target_branch={AllyTargetBranchSize} " +
                  $"enemy_target_branch={EnemyTargetBranchSize} map_object_target_branch={MapObjectTargetBranchSize} " +
                  $"allies={MaxObservedAllies} enemies={MaxObservedEnemies} " +
                  $"moving_asteroids={MaxObservedCollisionAsteroids} mining_asteroids={MaxObservedMiningAsteroids} " +
                  $"map_objects={MaxObservedMapObjects} navigation_grid={NavigationGridSize}x{NavigationGridSize} " +
                  $"weapon_slots={MaxWeaponSlots} enemy_weapon_mounts={MaxObservedEnemyWeaponMounts} " +
                  $"objective_channels={ObjectiveObservationSize} spawned_ship_control=dynamic");
    }

    private static int AgentCountKey(int side, int teamId)
    {
        return side * 4 + teamId;
    }

    private static void IncrementAgentCount(int side, int teamId)
    {
        int key = AgentCountKey(side, teamId);
        AgentCounts.TryGetValue(key, out int count);
        AgentCounts[key] = count + 1;
    }

    private static void DecrementAgentCount(int side, int teamId)
    {
        int key = AgentCountKey(side, teamId);
        if (!AgentCounts.TryGetValue(key, out int count))
        {
            return;
        }
        if (count <= 1)
        {
            AgentCounts.Remove(key);
        }
        else
        {
            AgentCounts[key] = count - 1;
        }
    }

    private static int GetAgentCount(int side, int teamId)
    {
        AgentCounts.TryGetValue(AgentCountKey(side, teamId), out int count);
        return count;
    }

    internal static int[] CreateDiscreteBranchSizes()
    {
        int[] branchSizes = new int[DiscreteBranchCount];
        for (int slot = 0; slot < WeaponFireBranchCount; slot++)
        {
            branchSizes[WeaponFireBranchStart + slot] = WeaponFireBranchSize;
        }
        branchSizes[SpecialActionBranch] = SpecialActionBranchSize;
        branchSizes[AllyTargetBranch] = AllyTargetBranchSize;
        branchSizes[EnemyTargetBranch] = EnemyTargetBranchSize;
        branchSizes[MapObjectTargetBranch] = MapObjectTargetBranchSize;
        return branchSizes;
    }

    private static void CreateAgent(Stage stage, int side, int teamId, string label)
    {
        GameObject obj = new GameObject($"RL Combat Agent - {label}");
        obj.transform.SetParent(stage.transform, false);

        BehaviorParameters behavior = obj.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = BehaviorName;
        behavior.TeamId = teamId;
        behavior.BrainParameters.VectorObservationSize = ObservationSize;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = new ActionSpec(
            ContinuousActionCount,
            CreateDiscreteBranchSizes());

        RlOneVsOneAgent agent = obj.AddComponent<RlOneVsOneAgent>();
        agent._stage = stage;
        agent._side = side;
        agent._teamId = teamId;
        IncrementAgentCount(side, teamId);
    }

    private static void ProvisionAgentsForSpawnedShips(Stage stage)
    {
        if (stage == null || ConfigData.Configuration == null ||
            Time.frameCount == _lastProvisionFrame || Time.frameCount % DecisionPeriod != 0)
        {
            return;
        }

        _lastProvisionFrame = Time.frameCount;
        Level level = stage.PrimaryLevel;
        if (level == null || level.State == null)
        {
            return;
        }

        int beeSide = ConfigData.Configuration.BeeSide;
        int humanSide = ConfigData.Configuration.HumanSide;
        int beeRequired = Mathf.Max(RlOneVsOneTrainingBootstrap.CurrentShipsPerSide, CountPolicyControlledShips(level, beeSide));
        int humanRequired = Mathf.Max(RlOneVsOneTrainingBootstrap.CurrentShipsPerSide, CountPolicyControlledShips(level, humanSide));
        EnsureAgentCount(stage, beeSide, 0, beeRequired);
        EnsureAgentCount(stage, beeSide, 1, beeRequired);
        EnsureAgentCount(stage, humanSide, 0, humanRequired);
        EnsureAgentCount(stage, humanSide, 1, humanRequired);
    }

    private static int CountPolicyControlledShips(Level level, int side)
    {
        int count = 0;
        List<Ship> ships = level.State.GetShips(side);
        for (int i = 0; i < ships.Count; i++)
        {
            if (RequiresPolicyControl(ships[i]))
            {
                count++;
            }
        }
        return count;
    }

    internal static bool RequiresPolicyControl(Ship ship)
    {
        return ship != null && !ship.IsDead &&
               (ship.IsMobile || ship.HasWeapons || HasSpecialAction(ship) ||
                CanUseMiningAction(ship) || CanUseHealingAction(ship) || CanUseWarpAction(ship));
    }

    private static void EnsureAgentCount(Stage stage, int side, int teamId, int required)
    {
        int existing = GetAgentCount(side, teamId);
        for (int slot = existing; slot < required; slot++)
        {
            CreateAgent(stage, side, teamId, $"Dynamic Side {side} Team {teamId} Slot {slot}");
        }
    }

    public override void Initialize()
    {
        ResetWeaponAimDirections();
        Instances.Add(this);
        RlOneVsOneEpisodeCoordinator.TsvRewardOccurred += HandleTsvRewardOccurred;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    protected override void OnDisable()
    {
        Instances.Remove(this);
        RlOneVsOneEpisodeCoordinator.TsvRewardOccurred -= HandleTsvRewardOccurred;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded -= HandleEpisodeEnded;
        ReleaseShip();
        if (_side != 0)
        {
            DecrementAgentCount(_side, _teamId);
        }
        base.OnDisable();
    }

    public override void OnEpisodeBegin()
    {
        ReleaseShip();
        _hasBoundShip = false;
        _hasParticipatedThisEpisode = false;
        _boundRuntimeShipId = 0;
        _decisionCounter = 0;
        _nextMiningActionTime = 0f;
        _nextHealingActionTime = 0f;
        ResetWeaponAimDirections();
    }

    private void ResetWeaponAimDirections()
    {
        for (int slot = 0; slot < _weaponAimDirections.Length; slot++)
        {
            _weaponAimDirections[slot] = Vector2.up;
        }
    }

    private void FixedUpdate()
    {
        ProvisionAgentsForSpawnedShips(_stage);
        if (_stage == null || !_stage.IsTrainingNueralNetwork || !IsCurrentController() || !TryBindShip())
        {
            return;
        }

        _decisionCounter++;
        if (_decisionCounter >= DecisionPeriod)
        {
            _decisionCounter = 0;
            RequestDecision();
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (!IsCurrentController() || !TryBindShip())
        {
            AddZeroObservations(sensor, ObservationSize);
            return;
        }

        _perception.Collect(_ship, _side, sensor);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        bool canControl = IsCurrentController() && TryBindShip();
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            bool enabled = canControl && HasTurretForSlot(slot);
            actionMask.SetActionEnabled(WeaponFireBranchStart + slot, FireWeaponAction, enabled);
        }

        actionMask.SetActionEnabled(SpecialActionBranch, ShipSpecialAction,
            canControl && HasSpecialAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, MiningAction,
            canControl && CanUseMiningAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, HealingAction,
            canControl && CanUseHealingAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, WarpAction,
            canControl && CanUseWarpAction(_ship));

        for (int action = 1; action < AllyTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(AllyTargetBranch, action, false);
        }
        for (int action = 1; action < EnemyTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(EnemyTargetBranch, action, false);
        }
        for (int action = 1; action < MapObjectTargetBranchSize; action++)
        {
            actionMask.SetActionEnabled(MapObjectTargetBranch, action, false);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!IsCurrentController() || !TryBindShip())
        {
            return;
        }

        var continuous = actions.ContinuousActions;
        ApplyMovement(new Vector2(continuous[0], continuous[1]));

        var discrete = actions.DiscreteActions;
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            int aimStart = WeaponAimContinuousActionStart + slot * WeaponAimContinuousActionsPerSlot;
            Vector2 aim = new Vector2(continuous[aimStart], continuous[aimStart + 1]);
            if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone)
            {
                _weaponAimDirections[slot] = aim.normalized;
            }

            bool fire = discrete[WeaponFireBranchStart + slot] == FireWeaponAction;
            ApplyWeaponCommand(slot, _weaponAimDirections[slot], fire);
        }

        switch (discrete[SpecialActionBranch])
        {
            case ShipSpecialAction:
                ApplySpecialAction();
                break;
            case MiningAction:
                TryApplyMiningAction();
                break;
            case HealingAction:
                TryApplyHealingAction();
                break;
            case WarpAction:
                TryApplyWarpAction();
                break;
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuous = actionsOut.ContinuousActions;
        for (int i = 0; i < ContinuousActionCount; i++)
        {
            continuous[i] = Random.Range(-1f, 1f);
        }
        var discrete = actionsOut.DiscreteActions;
        for (int slot = 0; slot < WeaponFireBranchCount; slot++)
        {
            discrete[WeaponFireBranchStart + slot] = Random.Range(0, WeaponFireBranchSize);
        }
        discrete[SpecialActionBranch] = Random.Range(0, SpecialActionBranchSize);
        discrete[AllyTargetBranch] = 0;
        discrete[EnemyTargetBranch] = 0;
        discrete[MapObjectTargetBranch] = 0;
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
        if (_ship.Weapons == null || slot < 0 || slot >= MaxWeaponSlots || slot >= _ship.Weapons.Count)
        {
            return;
        }

        if (!(_ship.Weapons[slot] is Turret turret))
        {
            return;
        }

        Vector2 target = turret.GetPosition() + aimDirection * Mathf.Max(1f, turret.Range);
        turret.SetRlControl(target, fire);
    }

    private bool HasTurretForSlot(int slot)
    {
        return _ship != null && _ship.Weapons != null && slot >= 0 && slot < MaxWeaponSlots &&
               slot < _ship.Weapons.Count && _ship.Weapons[slot] is Turret;
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

    internal static bool CanUseMiningAction(Ship ship)
    {
        return ship != null && !ship.IsDead &&
               (ship.ShipType == ConfigData.ShipTypes.Factory ||
                ship.ShipType == ConfigData.ShipTypes.CarpenterBee);
    }

    internal static bool CanUseHealingAction(Ship ship)
    {
        if (ship == null || ship.IsDead || ConfigData.Configuration == null ||
            ship.Side != ConfigData.Configuration.BeeSide)
        {
            return false;
        }

        if (!ConfigData.ShipSizes.TryGetValue(ship.ShipType, out Vector2Int shipSize) ||
            !ConfigData.ShipSizes.TryGetValue(ConfigData.ShipTypes.Beehive, out Vector2Int beehiveSize))
        {
            return false;
        }

        return shipSize.x < beehiveSize.x && shipSize.y < beehiveSize.y;
    }

    internal static bool CanUseWarpAction(Ship ship)
    {
        return ship != null && !ship.IsDead && ConfigData.Configuration != null &&
               ship.Side == ConfigData.Configuration.HumanSide &&
               ship.ShipType != ConfigData.ShipTypes.WarpGate;
    }

    private void TryApplyMiningAction()
    {
        if (!CanUseMiningAction(_ship) || Time.time < _nextMiningActionTime ||
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
        int oldTsv = _ship.Tsv;
        asteroid.Health -= amountMined;
        _ship.FleetShip.MineralsMinedThisLevel += amountMined;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        RewardSuccessfulCapabilityOutcome(_ship.Tsv - oldTsv);

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
        if (!CanUseHealingAction(_ship) || Time.time < _nextHealingActionTime ||
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
        int oldTsv = _ship.Tsv;
        _ship.Health += amountHealed;
        _ship.Tsv = Utilities.CalculateTsv(_ship);
        _ship.UpdateHealthBar();
        if (_ship.Level.HasPlayer)
        {
            beehive.SpawnHealingCross();
        }
        RewardSuccessfulCapabilityOutcome(_ship.Tsv - oldTsv);
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
        if (!CanUseWarpAction(_ship) || _ship.Level == null || _ship.Level.State == null || _ship.Collider == null)
        {
            return;
        }

        WarpGate warpGate = FindTouchingWarpGate();
        if (warpGate == null)
        {
            return;
        }

        int preservedTsv = Mathf.Max(0, _ship.Tsv);
        RewardSuccessfulCapabilityOutcome(preservedTsv);
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

    private void RewardSuccessfulCapabilityOutcome(int tsvValue)
    {
        if (tsvValue <= 0 || _ship == null || _ship.Level == null || _ship.Level.State == null)
        {
            return;
        }
        RlOneVsOneEpisodeCoordinator.RecordSuccessfulCapabilityOutcome(_ship, tsvValue);
    }

    private void HandleTsvRewardOccurred(int side, float reward)
    {
        if (side == _side && IsCurrentController() && _hasParticipatedThisEpisode)
        {
            AddReward(reward);
        }
    }

    private void HandleEpisodeEnded(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (result.EpisodeNumber <= _lastRewardedEpisode)
        {
            return;
        }

        _lastRewardedEpisode = result.EpisodeNumber;
        int assignedTeam = _side == ConfigData.Configuration.BeeSide ? result.BeeTeamId : result.HumanTeamId;
        if (_teamId != assignedTeam || !_hasParticipatedThisEpisode)
        {
            return;
        }

        AddReward(_side == ConfigData.Configuration.BeeSide
            ? result.BeeTerminalReward + result.BeeTimeReward
            : result.HumanTerminalReward + result.HumanTimeReward);
        if (result.TimedOut)
        {
            EpisodeInterrupted();
        }
        else
        {
            EndEpisode();
        }
    }

    private bool IsCurrentController()
    {
        return RlOneVsOneEpisodeCoordinator.IsControllerForSide(_side, _teamId);
    }

    private bool TryBindShip()
    {
        Level level = _stage != null ? _stage.PrimaryLevel : null;
        if (level == null || level.State == null)
        {
            ReleaseShip();
            return false;
        }

        if (_hasBoundShip)
        {
            if (_ship != null && !_ship.IsDead && _ship.Level == level && _ship.Id == _boundRuntimeShipId)
            {
                return true;
            }
            _ship = null;
            return false;
        }

        _bindCandidates.Clear();
        List<Ship> ships = level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (RequiresPolicyControl(candidate) && !IsControlledByAnotherAgent(candidate))
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
            ReportInvalidEnvironment(schemaError);
            _ship = null;
            return false;
        }
        if (!ValidateShipFitsArena(_ship))
        {
            _ship = null;
            return false;
        }

        _boundRuntimeShipId = _ship.Id;
        _hasBoundShip = true;
        _hasParticipatedThisEpisode = true;
        _nextMiningActionTime = 0f;
        _nextHealingActionTime = 0f;
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
            RlOneVsOneAgent other = Instances[i];
            if (other != null && other != this && other._side == _side && other._teamId == _teamId &&
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

    private bool ValidateShipFitsArena(Ship ship)
    {
        float extent = Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight());
        if (RlOneVsOneTrainingBootstrap.CurrentMapSize > extent * 2f)
        {
            return true;
        }

        ReportInvalidEnvironment($"RL arena size {RlOneVsOneTrainingBootstrap.CurrentMapSize:0.###} cannot contain " +
                                 $"{ship.ShipType} (diameter {extent * 2f:0.###}).");
        return false;
    }

    private void ReportInvalidEnvironment(string message)
    {
        if (_invalidEnvironmentReported)
        {
            return;
        }

        _invalidEnvironmentReported = true;
        Debug.LogError(message);
        if (_stage != null)
        {
            _stage.IsTrainingNueralNetwork = false;
        }
        if (!Application.isEditor)
        {
            Application.Quit(3);
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

    internal static float SquashSignedDistance(float value)
    {
        return RlCombatPerception.SquashSignedDistance(value);
    }

    private static void AddZeroObservations(VectorSensor sensor, int count)
    {
        for (int i = 0; i < count; i++)
        {
            sensor.AddObservation(0f);
        }
    }
}
