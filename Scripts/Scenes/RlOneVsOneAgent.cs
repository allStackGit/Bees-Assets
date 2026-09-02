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
    internal const int ShipTypeBitCount = 5;
    internal const int WeaponTypeBitCount = 4;
    internal const int MapObjectTypeBitCount = 4;
    internal const int ObstacleTypeBitCount = 4;
    internal const int MaxObservedAllies = RlOneVsOneTrainingOptions.MaximumShipsPerSide - 1;
    internal const int MaxObservedEnemies = RlOneVsOneTrainingOptions.MaximumShipsPerSide;
    internal const int MaxObservedMapObjects = 16;
    internal const int MaxObservedObstacles = 64;
    internal const int MaxWeaponSlots = 8;
    internal const int SelfObservationSize = 25;
    internal const int EntityObservationSize = 18;
    internal const int WeaponObservationSize = 17;
    internal const int MapObjectObservationSize = 12;
    internal const int ObstacleObservationSize = 15;
    internal const int ObservationSize = SelfObservationSize +
        (MaxObservedAllies + MaxObservedEnemies) * EntityObservationSize +
        MaxWeaponSlots * WeaponObservationSize +
        MaxObservedMapObjects * MapObjectObservationSize +
        MaxObservedObstacles * ObstacleObservationSize;

    // Movement/aim remain continuous. The capability branch is intentionally primitive: it asks
    // the ship to perform an action at its CURRENT location. It never invokes a Hive Mind command
    // and never scripts movement toward an asteroid, Beehive or Warp Gate.
    internal const int ContinuousActionCount = 4; // move x/y, aim x/y
    internal const int WeaponCommandBranch = 0;
    internal const int SpecialActionBranch = 1;
    internal const int AllyTargetBranch = 2;
    internal const int EnemyTargetBranch = 3;
    internal const int MapObjectTargetBranch = 4;
    internal const int WeaponCommandBranchSize = 1 + MaxWeaponSlots * 2; // none + (cease/fire) per slot
    internal const int NoSpecialAction = 0;
    internal const int ShipSpecialAction = 1;
    internal const int MiningAction = 2;
    internal const int HealingAction = 3;
    internal const int WarpAction = 4;
    internal const int SpecialActionBranchSize = 5;
    internal const int AllyTargetBranchSize = 1 + MaxObservedAllies;
    internal const int EnemyTargetBranchSize = 1 + MaxObservedEnemies;
    internal const int MapObjectTargetBranchSize = 1 + MaxObservedMapObjects;

    private const int MiningAsteroidObservationType = 1;
    private const int GenericMapObjectObservationType = 2;
    private const int FireTankObservationType = 3;
    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;
    private const float LocalDistanceScale = 40f;
    private const float MiningActionIntervalSeconds = 5f;
    private const float HealingActionIntervalSeconds = 1f;
    private const int HealingPerSuccessfulAction = 50;

    private readonly struct ObservedMapObject
    {
        internal readonly int Id;
        internal readonly int Type;
        internal readonly Vector2 Position;
        internal readonly Vector2 HalfExtents;
        internal readonly float HealthFraction;
        internal readonly float Activity;
        internal readonly bool Targetable;

        internal ObservedMapObject(
            int id,
            int type,
            Vector2 position,
            Vector2 halfExtents,
            float healthFraction,
            float activity,
            bool targetable)
        {
            Id = id;
            Type = type;
            Position = position;
            HalfExtents = halfExtents;
            HealthFraction = healthFraction;
            Activity = activity;
            Targetable = targetable;
        }
    }

    private readonly struct ObservedObstacle
    {
        internal readonly int Id;
        internal readonly int Type;
        internal readonly Vector2 Position;
        internal readonly Vector2 HalfExtents;
        internal readonly float Rotation;
        internal readonly Vector2 Velocity;
        internal readonly float HealthFraction;
        internal readonly bool Targetable;

        internal ObservedObstacle(
            int id,
            int type,
            Vector2 position,
            Vector2 halfExtents,
            float rotation,
            Vector2 velocity,
            float healthFraction,
            bool targetable)
        {
            Id = id;
            Type = type;
            Position = position;
            HalfExtents = halfExtents;
            Rotation = rotation;
            Velocity = velocity;
            HealthFraction = healthFraction;
            Targetable = targetable;
        }
    }

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
    private Vector2 _lastAimDirection = Vector2.up;
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly List<Ship> _allyCandidates = new List<Ship>();
    private readonly List<Ship> _enemyCandidates = new List<Ship>();
    private readonly List<ObservedMapObject> _mapObjectCandidates = new List<ObservedMapObject>();
    private readonly List<ObservedObstacle> _obstacleCandidates = new List<ObservedObstacle>();

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

        Debug.Log($"RL combat policy schema observations={ObservationSize} continuous_actions={ContinuousActionCount} " +
                  $"discrete_branches=[{WeaponCommandBranchSize},{SpecialActionBranchSize}," +
                  $"{AllyTargetBranchSize},{EnemyTargetBranchSize},{MapObjectTargetBranchSize}] " +
                  $"allies={MaxObservedAllies} enemies={MaxObservedEnemies} map_objects={MaxObservedMapObjects} " +
                  $"obstacles={MaxObservedObstacles} weapon_slots={MaxWeaponSlots} spawned_ship_control=dynamic");
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
            new[]
            {
                WeaponCommandBranchSize,
                SpecialActionBranchSize,
                AllyTargetBranchSize,
                EnemyTargetBranchSize,
                MapObjectTargetBranchSize
            });

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
               (ship.IsMobile || ship.HasWeapons || HasSpecialAction(ship));
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
        _lastAimDirection = Vector2.up;
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

        Vector2 origin = _ship.GetPosition();
        AddSelfObservations(sensor, origin);
        CollectAllies(origin);
        AddEntitySlots(sensor, _allyCandidates, MaxObservedAllies, origin);
        CollectVisibleEnemies(origin);
        AddEntitySlots(sensor, _enemyCandidates, MaxObservedEnemies, origin);
        AddWeaponSlots(sensor, origin);
        CollectVisibleMapObjects(origin);
        AddMapObjectSlots(sensor, origin);
        CollectVisibleObstacles(origin);
        AddObstacleSlots(sensor, origin);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        bool canControl = IsCurrentController() && TryBindShip();
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            bool enabled = canControl && HasTurretForSlot(slot);
            actionMask.SetActionEnabled(WeaponCommandBranch, 1 + slot * 2, enabled);
            actionMask.SetActionEnabled(WeaponCommandBranch, 2 + slot * 2, enabled);
        }

        // Masks describe permanent capability only. They deliberately do NOT reveal whether the ship
        // is currently touching a valid target, damaged, off cooldown, or otherwise in a successful
        // situation. Invalid attempts remain legal and simply have no effect.
        actionMask.SetActionEnabled(SpecialActionBranch, ShipSpecialAction,
            canControl && HasSpecialAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, MiningAction,
            canControl && CanUseMiningAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, HealingAction,
            canControl && CanUseHealingAction(_ship));
        actionMask.SetActionEnabled(SpecialActionBranch, WarpAction,
            canControl && CanUseWarpAction(_ship));

        // These branches remain reserved permanent capacity for a future mechanic that genuinely
        // needs explicit entity selection. Mine/heal/warp are spatial primitive actions and do not
        // consume target selections.
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
        Vector2 aim = new Vector2(continuous[2], continuous[3]);
        if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone)
        {
            _lastAimDirection = aim.normalized;
        }

        var discrete = actions.DiscreteActions;
        int weaponCommand = discrete[WeaponCommandBranch];
        if (weaponCommand > 0)
        {
            int encoded = weaponCommand - 1;
            ApplyWeaponCommand(encoded / 2, (encoded & 1) == 1);
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
        discrete[WeaponCommandBranch] = Random.Range(0, WeaponCommandBranchSize);
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

    private void ApplyWeaponCommand(int slot, bool fire)
    {
        if (_ship.Weapons == null || slot < 0 || slot >= MaxWeaponSlots)
        {
            return;
        }

        for (int i = 0; i < _ship.Weapons.Count; i++)
        {
            if (Mathf.Min(i, MaxWeaponSlots - 1) != slot || !(_ship.Weapons[i] is Turret turret))
            {
                continue;
            }
            Vector2 target = turret.GetPosition() + _lastAimDirection * Mathf.Max(1f, turret.Range);
            turret.SetRlControl(target, fire);
        }
    }

    private bool HasTurretForSlot(int slot)
    {
        if (_ship == null || _ship.Weapons == null)
        {
            return false;
        }
        for (int i = 0; i < _ship.Weapons.Count; i++)
        {
            if (Mathf.Min(i, MaxWeaponSlots - 1) == slot && _ship.Weapons[i] is Turret)
            {
                return true;
            }
        }
        return false;
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

        // Healing eligibility is an inherent ship-size rule, not a statement about whether a live
        // Beehive exists nearby. Requiring both dimensions to be strictly smaller also excludes the
        // Beehive itself without a special-case positional check.
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
            _ship = null; // One trajectory owns one physical ship lifecycle.
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

        if (!_invalidEnvironmentReported)
        {
            _invalidEnvironmentReported = true;
            Debug.LogError($"RL arena size {RlOneVsOneTrainingBootstrap.CurrentMapSize:0.###} cannot contain " +
                           $"{ship.ShipType} (diameter {extent * 2f:0.###}).");
            if (_stage != null)
            {
                _stage.IsTrainingNueralNetwork = false;
            }
            if (!Application.isEditor)
            {
                Application.Quit(3);
            }
        }
        return false;
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

    private void AddSelfObservations(VectorSensor sensor, Vector2 position)
    {
        AddEnumBits(sensor, (int)_ship.ShipType, ShipTypeBitCount);
        float halfMap = Mathf.Max(1f, RlOneVsOneTrainingBootstrap.CurrentMapSize * 0.5f);
        sensor.AddObservation(Mathf.Clamp(position.x / halfMap, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(position.y / halfMap, -1f, 1f));
        sensor.AddObservation(NormalizePositive(RlOneVsOneTrainingBootstrap.CurrentMapSize, 30f));
        AddHeading(sensor, _ship.Rotation);
        sensor.AddObservation(GetHealthFraction(_ship));
        sensor.AddObservation(NormalizePositive(_ship.Speed, 20f));
        sensor.AddObservation(NormalizePositive(_ship.CurrentSpeed, 20f));
        sensor.AddObservation(NormalizePositive(_ship.RotationSpeed, 240f));
        sensor.AddObservation(NormalizePositive(_ship.LongestSide, 10f));
        sensor.AddObservation(NormalizePositive(_ship.Sight, 80f));
        sensor.AddObservation(NormalizePositive(_ship.MaxRange, 80f));
        sensor.AddObservation(NormalizePositive(_ship.Firepower, 200f));
        sensor.AddObservation(_ship.IsMobile ? 1f : 0f);
        sensor.AddObservation(_ship.IsBomber ? 1f : 0f);
        sensor.AddObservation(_ship.IsCarrierShip ? 1f : 0f);
        sensor.AddObservation(_ship.HasWeapons ? 1f : 0f);
        sensor.AddObservation(_ship.HasTurrets ? 1f : 0f);
        sensor.AddObservation(HasSpecialAction(_ship) ? 1f : 0f);
        sensor.AddObservation(GetSpecialReadiness(_ship));
    }

    private void CollectAllies(Vector2 origin)
    {
        _allyCandidates.Clear();
        List<Ship> ships = _ship.Level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (candidate != null && candidate != _ship && !candidate.IsDead)
            {
                _allyCandidates.Add(candidate);
            }
        }
        SortShipsForObservation(_allyCandidates, origin);
    }

    private void CollectVisibleEnemies(Vector2 origin)
    {
        _enemyCandidates.Clear();
        foreach (Ship candidate in _ship.Level.State.GetShipsVisibleToHiveMind(_side))
        {
            if (candidate != null && !candidate.IsDead && candidate.Side != _side)
            {
                _enemyCandidates.Add(candidate);
            }
        }
        SortShipsForObservation(_enemyCandidates, origin);
    }

    private static void SortShipsForObservation(List<Ship> ships, Vector2 origin)
    {
        ships.Sort((left, right) =>
        {
            int compare = (left.GetPosition() - origin).sqrMagnitude.CompareTo(
                (right.GetPosition() - origin).sqrMagnitude);
            if (compare != 0)
            {
                return compare;
            }
            compare = ((int)left.ShipType).CompareTo((int)right.ShipType);
            if (compare != 0)
            {
                return compare;
            }
            long leftFleetId = left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
            long rightFleetId = right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
            compare = leftFleetId.CompareTo(rightFleetId);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private static void AddEntitySlots(VectorSensor sensor, List<Ship> ships, int slots, Vector2 origin)
    {
        for (int slot = 0; slot < slots; slot++)
        {
            if (slot >= ships.Count)
            {
                AddZeroObservations(sensor, EntityObservationSize);
                continue;
            }

            Ship ship = ships[slot];
            Vector2 relative = ship.GetPosition() - origin;
            sensor.AddObservation(1f);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            AddHeading(sensor, ship.Rotation);
            sensor.AddObservation(GetHealthFraction(ship));
            sensor.AddObservation(NormalizePositive(ship.Speed, 20f));
            sensor.AddObservation(NormalizePositive(ship.CurrentSpeed, 20f));
            sensor.AddObservation(NormalizePositive(ship.LongestSide, 10f));
            sensor.AddObservation(NormalizePositive(ship.MaxRange, 80f));
            sensor.AddObservation(NormalizePositive(ship.Firepower, 200f));
            sensor.AddObservation(ship.IsMobile ? 1f : 0f);
            sensor.AddObservation(ship.IsBomber ? 1f : 0f);
            AddEnumBits(sensor, (int)ship.ShipType, ShipTypeBitCount);
        }
    }

    private void AddWeaponSlots(VectorSensor sensor, Vector2 origin)
    {
        // Weapon is an authored List rather than an unordered set. Its serialized/setup order is the
        // stable slot identity, so do not re-sort it by transient range/target state.
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            if (_ship.Weapons == null || slot >= _ship.Weapons.Count || _ship.Weapons[slot] == null)
            {
                AddZeroObservations(sensor, WeaponObservationSize);
                continue;
            }

            Weapon weapon = _ship.Weapons[slot];
            sensor.AddObservation(1f);
            AddEnumBits(sensor, (int)weapon.Type, WeaponTypeBitCount);
            Vector2 relative = weapon.GetPosition() - origin;
            float size = Mathf.Max(1f, _ship.LongestSide);
            sensor.AddObservation(Mathf.Clamp(relative.x / size, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(relative.y / size, -1f, 1f));
            sensor.AddObservation(NormalizePositive(weapon.Range, 80f));
            sensor.AddObservation(NormalizePositive(weapon.Power, 100f));
            sensor.AddObservation(NormalizePositive(weapon.RateOfFire, 5f));
            sensor.AddObservation(NormalizePositive(weapon.RotationRate, 240f));
            sensor.AddObservation(NormalizePositive(weapon.ProjectileValue, 2f));

            if (weapon is Turret turret)
            {
                sensor.AddObservation(1f);
                AddHeading(sensor, turret.Rotation);
                sensor.AddObservation(turret.ReadyToFire ? 1f : 0f);
                sensor.AddObservation(turret.IsAimedAtTarget ? 1f : 0f);
            }
            else
            {
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(0f);
                sensor.AddObservation(weapon.HasTargetShip ? 1f : 0f);
                sensor.AddObservation(0f);
            }
        }
    }

    private void CollectVisibleMapObjects(Vector2 origin)
    {
        _mapObjectCandidates.Clear();
        GameState state = _ship.Level.State;
        foreach (MiningAsteroid asteroid in state.GetMiningAsteroidsVisibleToHiveMind(_side))
        {
            if (asteroid == null || asteroid.IsDead)
            {
                continue;
            }

            Collider2D collider = asteroid.ClearanceMappingCollider != null
                ? asteroid.ClearanceMappingCollider
                : asteroid.Collider;
            GetColliderGeometry(_ship.Level, collider, asteroid.GetPosition(), out Vector2 position, out Vector2 halfExtents);
            _mapObjectCandidates.Add(new ObservedMapObject(
                asteroid.Id,
                MiningAsteroidObservationType,
                position,
                halfExtents,
                GetMiningAsteroidResourceFraction(asteroid),
                asteroid.SquadsMining.Count,
                false));
        }

        foreach (MapObject mapObject in state.GetMapObjectsVisibleToHiveMind(_side))
        {
            if (mapObject == null || mapObject.IsDead)
            {
                continue;
            }

            GetColliderGeometry(
                _ship.Level,
                mapObject.Collider,
                mapObject.transform.localPosition,
                out Vector2 position,
                out Vector2 halfExtents);
            int type = mapObject is CanisterBomb
                ? FireTankObservationType
                : GenericMapObjectObservationType;
            float healthFraction = mapObject.MaxHealth > 0
                ? Mathf.Clamp01((float)mapObject.Health / mapObject.MaxHealth)
                : 0f;
            _mapObjectCandidates.Add(new ObservedMapObject(
                mapObject.Id,
                type,
                position,
                halfExtents,
                healthFraction,
                0f,
                true));
        }

        _mapObjectCandidates.Sort((left, right) =>
        {
            int compare = (left.Position - origin).sqrMagnitude.CompareTo((right.Position - origin).sqrMagnitude);
            if (compare != 0)
            {
                return compare;
            }
            compare = left.Type.CompareTo(right.Type);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private void AddMapObjectSlots(VectorSensor sensor, Vector2 origin)
    {
        for (int slot = 0; slot < MaxObservedMapObjects; slot++)
        {
            if (slot >= _mapObjectCandidates.Count)
            {
                AddZeroObservations(sensor, MapObjectObservationSize);
                continue;
            }

            ObservedMapObject mapObject = _mapObjectCandidates[slot];
            Vector2 relative = mapObject.Position - origin;
            sensor.AddObservation(1f);
            AddEnumBits(sensor, mapObject.Type, MapObjectTypeBitCount);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            sensor.AddObservation(mapObject.HealthFraction);
            sensor.AddObservation(NormalizePositive(mapObject.HalfExtents.x, 20f));
            sensor.AddObservation(NormalizePositive(mapObject.HalfExtents.y, 20f));
            sensor.AddObservation(mapObject.Targetable ? 1f : 0f);
            sensor.AddObservation(NormalizePositive(mapObject.Activity, 4f));
        }
    }

    private void CollectVisibleObstacles(Vector2 origin)
    {
        _obstacleCandidates.Clear();
        foreach (Obstacle obstacle in _ship.Level.State.GetObstaclesVisibleToHiveMind(_side))
        {
            if (obstacle == null || obstacle.IsDead || obstacle is MiningAsteroid || obstacle is AsteroidPiece)
            {
                continue;
            }

            Collider2D collider = obstacle.ClearanceMappingCollider != null
                ? obstacle.ClearanceMappingCollider
                : obstacle.Collider;
            GetColliderGeometry(_ship.Level, collider, obstacle.GetPosition(), out Vector2 position, out Vector2 halfExtents);

            Vector2 velocity = Vector2.zero;
            if (obstacle is CollisionAsteroid collisionAsteroid && collisionAsteroid.Body != null)
            {
                velocity = GetLevelLocalVelocity(_ship.Level, collisionAsteroid.Body.linearVelocity);
            }

            float healthFraction = obstacle.OriginalHealth > 0
                ? Mathf.Clamp01((float)obstacle.Health / obstacle.OriginalHealth)
                : 0f;
            _obstacleCandidates.Add(new ObservedObstacle(
                obstacle.Id,
                (int)obstacle.ObstacleType,
                position,
                halfExtents,
                obstacle.transform.localEulerAngles.z,
                velocity,
                healthFraction,
                obstacle.ObstacleType == ConfigData.ObstacleTypes.CollisionAsteroid));
        }

        _obstacleCandidates.Sort((left, right) =>
        {
            int compare = DistanceSquaredToBounds(left, origin).CompareTo(DistanceSquaredToBounds(right, origin));
            if (compare != 0)
            {
                return compare;
            }
            compare = left.Type.CompareTo(right.Type);
            return compare != 0 ? compare : left.Id.CompareTo(right.Id);
        });
    }

    private void AddObstacleSlots(VectorSensor sensor, Vector2 origin)
    {
        for (int slot = 0; slot < MaxObservedObstacles; slot++)
        {
            if (slot >= _obstacleCandidates.Count)
            {
                AddZeroObservations(sensor, ObstacleObservationSize);
                continue;
            }

            ObservedObstacle obstacle = _obstacleCandidates[slot];
            Vector2 relative = obstacle.Position - origin;
            sensor.AddObservation(1f);
            AddEnumBits(sensor, obstacle.Type, ObstacleTypeBitCount);
            sensor.AddObservation(SquashSignedDistance(relative.x));
            sensor.AddObservation(SquashSignedDistance(relative.y));
            sensor.AddObservation(NormalizePositive(obstacle.HalfExtents.x, 20f));
            sensor.AddObservation(NormalizePositive(obstacle.HalfExtents.y, 20f));
            AddHeading(sensor, obstacle.Rotation);
            sensor.AddObservation(SquashSignedDistance(obstacle.Velocity.x));
            sensor.AddObservation(SquashSignedDistance(obstacle.Velocity.y));
            sensor.AddObservation(obstacle.HealthFraction);
            sensor.AddObservation(obstacle.Targetable ? 1f : 0f);
        }
    }

    private static void GetColliderGeometry(
        Level level,
        Collider2D collider,
        Vector2 fallbackPosition,
        out Vector2 position,
        out Vector2 halfExtents)
    {
        position = fallbackPosition;
        halfExtents = Vector2.zero;
        if (collider == null || !collider.enabled)
        {
            return;
        }

        Bounds bounds = collider.bounds;
        Vector2 min = PathfinderObstacleScope.WorldToLevel(level, bounds.min);
        Vector2 max = PathfinderObstacleScope.WorldToLevel(level, bounds.max);
        position = (min + max) * 0.5f;
        halfExtents = new Vector2(
            Mathf.Abs(max.x - min.x) * 0.5f,
            Mathf.Abs(max.y - min.y) * 0.5f);
    }

    private static Vector2 GetLevelLocalVelocity(Level level, Vector2 worldVelocity)
    {
        Transform mapTransform = level?.Map?.Transform;
        return mapTransform != null
            ? (Vector2)mapTransform.InverseTransformVector(worldVelocity)
            : worldVelocity;
    }

    private static float DistanceSquaredToBounds(ObservedObstacle obstacle, Vector2 origin)
    {
        Vector2 relative = obstacle.Position - origin;
        float dx = Mathf.Max(0f, Mathf.Abs(relative.x) - obstacle.HalfExtents.x);
        float dy = Mathf.Max(0f, Mathf.Abs(relative.y) - obstacle.HalfExtents.y);
        return dx * dx + dy * dy;
    }

    private Ship FindNearestVisibleEnemy()
    {
        if (_ship == null)
        {
            return null;
        }
        CollectVisibleEnemies(_ship.GetPosition());
        return _enemyCandidates.Count > 0 ? _enemyCandidates[0] : null;
    }

    private static bool HasSpecialAction(Ship ship)
    {
        return ship is YellowJacket || ship is Striker || ship is FireBarge || ship is Barge || ship is Scout;
    }

    private static float GetSpecialReadiness(Ship ship)
    {
        // Readiness is limited to intrinsic cooldown/state. Spatial validity is learned from the
        // observations and consequences, not exposed through either masks or this scalar.
        if (ship is YellowJacket)
        {
            return 1f;
        }
        if (ship is Striker striker)
        {
            return striker.IsBombReady ? 1f : 0f;
        }
        if (ship is Barge barge)
        {
            return !barge.HasStartedCharging && !barge.IsCharging ? 1f : 0f;
        }
        if (ship is FireBarge)
        {
            return 1f;
        }
        if (ship is Scout scout)
        {
            return scout.IsBeaconReady ? 1f : 0f;
        }
        return 0f;
    }

    private static float GetHealthFraction(Ship ship)
    {
        return ship != null && ship.MaxHealth > 0
            ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth)
            : 0f;
    }

    private static float GetMiningAsteroidResourceFraction(MiningAsteroid asteroid)
    {
        return asteroid != null && asteroid.OriginalHealth > 0
            ? Mathf.Clamp01((float)asteroid.Health / asteroid.OriginalHealth)
            : 0f;
    }

    internal static float SquashSignedDistance(float value)
    {
        float absolute = Mathf.Abs(value);
        return absolute <= 0f ? 0f : Mathf.Sign(value) * absolute / (absolute + LocalDistanceScale);
    }

    private static float NormalizePositive(float value, float scale)
    {
        float positive = Mathf.Max(0f, value);
        return positive <= 0f ? 0f : positive / (positive + Mathf.Max(0.0001f, scale));
    }

    private static void AddEnumBits(VectorSensor sensor, int value, int bits)
    {
        for (int bit = 0; bit < bits; bit++)
        {
            sensor.AddObservation((value & (1 << bit)) != 0 ? 1f : 0f);
        }
    }

    private static void AddHeading(VectorSensor sensor, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        sensor.AddObservation(Mathf.Sin(radians));
        sensor.AddObservation(Mathf.Cos(radians));
    }

    private static void AddZeroObservations(VectorSensor sensor, int count)
    {
        for (int i = 0; i < count; i++)
        {
            sensor.AddObservation(0f);
        }
    }
}
