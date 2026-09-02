using Assets.Scripts;
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
/// Final-generation shared combat policy adapter. The observation/action shape is deliberately fixed
/// for the full combat-training lifetime: extra allies/enemies/weapons occupy masked zero slots rather
/// than changing the neural-network contract when the curriculum expands.
/// </summary>
internal sealed class RlOneVsOneAgent : Agent
{
    internal const string BehaviorName = "BeesRL1v1";
    internal const int DecisionPeriod = 5;

    internal const int ShipTypeBitCount = 5;
    internal const int WeaponTypeBitCount = 4;
    internal const int MaxObservedAllies = RlOneVsOneTrainingOptions.MaximumShipsPerSide - 1;
    internal const int MaxObservedEnemies = RlOneVsOneTrainingOptions.MaximumShipsPerSide;
    internal const int MaxWeaponSlots = 8;
    internal const int MaxControlledShipsPerSide = RlOneVsOneTrainingOptions.MaximumShipsPerSide * 2;

    internal const int SelfObservationSize = 25;
    internal const int EntityObservationSize = 18;
    internal const int WeaponObservationSize = 17;
    internal const int ObservationSize = SelfObservationSize +
        (MaxObservedAllies + MaxObservedEnemies) * EntityObservationSize +
        MaxWeaponSlots * WeaponObservationSize;

    internal const int MovementActionCount = 2;
    internal const int ActionsPerWeaponSlot = 3;
    internal const int SpecialActionCount = 1;
    internal const int ContinuousActionCount = MovementActionCount +
        MaxWeaponSlots * ActionsPerWeaponSlot + SpecialActionCount;

    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;
    private const float LocalDistanceScale = 40f;

    private static readonly List<RlOneVsOneAgent> Instances = new List<RlOneVsOneAgent>();
    private static bool _invalidEnvironmentReported;

    private Stage _stage;
    private Ship _ship;
    private int _side;
    private int _teamId;
    private int _shipSlot;
    private int _decisionCounter;
    private int _lastRewardedEpisode;
    private bool _hasBoundShip;
    private bool _hasParticipatedThisEpisode;
    private int _boundRuntimeShipId;

    private readonly Vector2[] _lastAimDirections = new Vector2[MaxWeaponSlots];
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly List<Ship> _allyCandidates = new List<Ship>();
    private readonly List<Ship> _enemyCandidates = new List<Ship>();

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

        // Reserve enough fixed Agent wrappers for the authored starting fleet plus units that may be
        // created during an episode (Queen/minion-style mechanics). Idle wrappers never request a
        // decision and therefore do not create trajectories.
        for (int shipSlot = 0; shipSlot < MaxControlledShipsPerSide; shipSlot++)
        {
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 0, shipSlot, $"Bee Team 0 Slot {shipSlot}");
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 1, shipSlot, $"Bee Team 1 Slot {shipSlot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 0, shipSlot, $"Human Team 0 Slot {shipSlot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 1, shipSlot, $"Human Team 1 Slot {shipSlot}");
        }

        Debug.Log($"RL combat policy schema observations={ObservationSize} actions={ContinuousActionCount} " +
                  $"allies={MaxObservedAllies} enemies={MaxObservedEnemies} weapon_slots={MaxWeaponSlots} " +
                  $"control_slots_per_side={MaxControlledShipsPerSide}");
    }

    private static void CreateAgent(Stage stage, int side, int teamId, int shipSlot, string label)
    {
        GameObject agentObject = new GameObject($"RL Combat Agent - {label}");
        agentObject.transform.SetParent(stage.transform, false);

        BehaviorParameters behavior = agentObject.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = BehaviorName;
        behavior.TeamId = teamId;
        behavior.BrainParameters.VectorObservationSize = ObservationSize;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ContinuousActionCount);

        RlOneVsOneAgent agent = agentObject.AddComponent<RlOneVsOneAgent>();
        agent.Configure(stage, side, teamId, shipSlot);
    }

    private void Configure(Stage stage, int side, int teamId, int shipSlot)
    {
        _stage = stage;
        _side = side;
        _teamId = teamId;
        _shipSlot = shipSlot;
    }

    public override void Initialize()
    {
        Instances.Add(this);
        RlOneVsOneEpisodeCoordinator.TsvRewardOccurred += HandleTsvRewardOccurred;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
        ResetAimDirections();
    }

    protected override void OnDisable()
    {
        Instances.Remove(this);
        RlOneVsOneEpisodeCoordinator.TsvRewardOccurred -= HandleTsvRewardOccurred;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded -= HandleEpisodeEnded;
        ReleaseShip();
        base.OnDisable();
    }

    public override void OnEpisodeBegin()
    {
        ReleaseShip();
        _hasBoundShip = false;
        _hasParticipatedThisEpisode = false;
        _boundRuntimeShipId = 0;
        _decisionCounter = 0;
        ResetAimDirections();
    }

    private void FixedUpdate()
    {
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

        Vector2 shipPosition = _ship.GetPosition();
        AddSelfObservations(sensor, shipPosition);

        CollectAllies(shipPosition);
        AddEntitySlots(sensor, _allyCandidates, MaxObservedAllies, shipPosition);

        CollectVisibleEnemies(shipPosition);
        AddEntitySlots(sensor, _enemyCandidates, MaxObservedEnemies, shipPosition);

        AddWeaponSlots(sensor, shipPosition);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!IsCurrentController() || !TryBindShip())
        {
            return;
        }

        var continuous = actions.ContinuousActions;
        ApplyMovement(new Vector2(continuous[0], continuous[1]));

        if (_ship.Weapons != null)
        {
            for (int weaponIndex = 0; weaponIndex < _ship.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = _ship.Weapons[weaponIndex];
                if (!(weapon is Turret turret))
                {
                    continue;
                }

                int slot = Mathf.Min(weaponIndex, MaxWeaponSlots - 1);
                int actionIndex = MovementActionCount + slot * ActionsPerWeaponSlot;
                Vector2 aim = new Vector2(continuous[actionIndex], continuous[actionIndex + 1]);
                if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone)
                {
                    _lastAimDirections[slot] = aim.normalized;
                }

                bool fireRequested = continuous[actionIndex + 2] > 0f;
                Vector2 targetPoint = turret.GetPosition() +
                    _lastAimDirections[slot] * Mathf.Max(1f, turret.Range);
                turret.SetRlControl(targetPoint, fireRequested);
            }
        }

        int specialActionIndex = MovementActionCount + MaxWeaponSlots * ActionsPerWeaponSlot;
        if (continuous[specialActionIndex] > 0f)
        {
            ApplySpecialAction();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuous = actionsOut.ContinuousActions;
        for (int i = 0; i < ContinuousActionCount; i++)
        {
            continuous[i] = Random.Range(-1f, 1f);
        }
    }

    private void ApplyMovement(Vector2 movement)
    {
        if (!_ship.IsMobile || _ship.CannotChangeMovementOrders ||
            movement.sqrMagnitude < MovementDeadZone * MovementDeadZone)
        {
            if (_ship.IsMobile && !_ship.CannotChangeMovementOrders)
            {
                _ship.Direction = 360;
            }
            _ship.HasBrain = true;
            return;
        }

        Vector2 movementPoint = _ship.GetPosition() + movement.normalized;
        int direction = Mathf.RoundToInt(_ship.GetDegreesTowardsPoint(movementPoint));
        _ship.Direction = ((direction % 360) + 360) % 360;
        _ship.HasBrain = true;
    }

    private void ApplySpecialAction()
    {
        // These abilities are not turret fire and therefore have a permanent dedicated action bit.
        // Adding another ship-specific adapter later does not change the neural action shape.
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
        int assignedTeamId = _side == ConfigData.Configuration.BeeSide
            ? result.BeeTeamId
            : result.HumanTeamId;
        if (_teamId != assignedTeamId || !_hasParticipatedThisEpisode)
        {
            // Fixed reserve wrappers that never controlled a ship must not generate zero-step games.
            return;
        }

        float reward = _side == ConfigData.Configuration.BeeSide
            ? result.BeeTerminalReward + result.BeeTimeReward
            : result.HumanTerminalReward + result.HumanTimeReward;
        AddReward(reward);

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

            // Never jump a participating wrapper to another ship after its ship dies. This preserves
            // one trajectory per physical ship lifecycle and keeps terminal team credit well-defined.
            _ship = null;
            return false;
        }

        _bindCandidates.Clear();
        List<Ship> ships = level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (candidate != null && !candidate.IsDead && !IsControlledByAnotherAgent(candidate))
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
            if (other == null || other == this || other._side != _side || other._teamId != _teamId)
            {
                continue;
            }
            if (other._ship == candidate && other._hasBoundShip)
            {
                return true;
            }
        }
        return false;
    }

    private static int CompareShipsForControl(Ship left, Ship right)
    {
        long leftFleetId = left != null && left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
        long rightFleetId = right != null && right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
        int fleetComparison = leftFleetId.CompareTo(rightFleetId);
        if (fleetComparison != 0)
        {
            return fleetComparison;
        }
        int leftRuntimeId = left != null ? left.Id : int.MaxValue;
        int rightRuntimeId = right != null ? right.Id : int.MaxValue;
        return leftRuntimeId.CompareTo(rightRuntimeId);
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
                           $"{ship.ShipType} (required diameter greater than {extent * 2f:0.###}).");
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

    private void AddSelfObservations(VectorSensor sensor, Vector2 shipPosition)
    {
        AddShipTypeBits(sensor, _ship.ShipType);

        float halfMap = Mathf.Max(1f, RlOneVsOneTrainingBootstrap.CurrentMapSize * 0.5f);
        sensor.AddObservation(Mathf.Clamp(shipPosition.x / halfMap, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(shipPosition.y / halfMap, -1f, 1f));
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

    private void CollectAllies(Vector2 shipPosition)
    {
        _allyCandidates.Clear();
        List<Ship> allies = _ship.Level.State.GetShips(_side);
        for (int i = 0; i < allies.Count; i++)
        {
            Ship candidate = allies[i];
            if (candidate != null && candidate != _ship && !candidate.IsDead)
            {
                _allyCandidates.Add(candidate);
            }
        }
        SortByDistanceThenId(_allyCandidates, shipPosition);
    }

    private void CollectVisibleEnemies(Vector2 shipPosition)
    {
        _enemyCandidates.Clear();
        foreach (Ship candidate in _ship.Level.State.GetShipsVisibleToHiveMind(_side))
        {
            if (candidate != null && !candidate.IsDead && candidate.Side != _side)
            {
                _enemyCandidates.Add(candidate);
            }
        }
        SortByDistanceThenId(_enemyCandidates, shipPosition);
    }

    private static void SortByDistanceThenId(List<Ship> ships, Vector2 origin)
    {
        ships.Sort((left, right) =>
        {
            float leftDistance = (left.GetPosition() - origin).sqrMagnitude;
            float rightDistance = (right.GetPosition() - origin).sqrMagnitude;
            int distanceComparison = leftDistance.CompareTo(rightDistance);
            return distanceComparison != 0 ? distanceComparison : left.Id.CompareTo(right.Id);
        });
    }

    private static void AddEntitySlots(VectorSensor sensor, List<Ship> ships, int slotCount, Vector2 origin)
    {
        for (int slot = 0; slot < slotCount; slot++)
        {
            if (slot >= ships.Count)
            {
                AddZeroObservations(sensor, EntityObservationSize);
                continue;
            }

            Ship ship = ships[slot];
            Vector2 relativePosition = ship.GetPosition() - origin;
            sensor.AddObservation(1f);
            sensor.AddObservation(SquashSignedDistance(relativePosition.x));
            sensor.AddObservation(SquashSignedDistance(relativePosition.y));
            AddHeading(sensor, ship.Rotation);
            sensor.AddObservation(GetHealthFraction(ship));
            sensor.AddObservation(NormalizePositive(ship.Speed, 20f));
            sensor.AddObservation(NormalizePositive(ship.CurrentSpeed, 20f));
            sensor.AddObservation(NormalizePositive(ship.LongestSide, 10f));
            sensor.AddObservation(NormalizePositive(ship.MaxRange, 80f));
            sensor.AddObservation(NormalizePositive(ship.Firepower, 200f));
            sensor.AddObservation(ship.IsMobile ? 1f : 0f);
            sensor.AddObservation(ship.IsBomber ? 1f : 0f);
            AddShipTypeBits(sensor, ship.ShipType);
        }
    }

    private void AddWeaponSlots(VectorSensor sensor, Vector2 shipPosition)
    {
        for (int slot = 0; slot < MaxWeaponSlots; slot++)
        {
            if (_ship.Weapons == null || slot >= _ship.Weapons.Count || _ship.Weapons[slot] == null)
            {
                AddZeroObservations(sensor, WeaponObservationSize);
                continue;
            }

            Weapon weapon = _ship.Weapons[slot];
            sensor.AddObservation(1f);
            AddWeaponTypeBits(sensor, weapon.Type);

            Vector2 relativeWeaponPosition = weapon.GetPosition() - shipPosition;
            float shipSizeScale = Mathf.Max(1f, _ship.LongestSide);
            sensor.AddObservation(Mathf.Clamp(relativeWeaponPosition.x / shipSizeScale, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(relativeWeaponPosition.y / shipSizeScale, -1f, 1f));
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
        return ship is YellowJacket || ship is Striker || ship is FireBarge || ship is Barge;
    }

    private static float GetSpecialReadiness(Ship ship)
    {
        if (ship is YellowJacket yellowJacket)
        {
            return yellowJacket.TouchingShip != null && !yellowJacket.TouchingShip.IsDead &&
                   yellowJacket.TouchingShip.Side != yellowJacket.Side ? 1f : 0f;
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
        return 0f;
    }

    private static float GetHealthFraction(Ship ship)
    {
        return ship != null && ship.MaxHealth > 0
            ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth)
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

    private static void AddShipTypeBits(VectorSensor sensor, ConfigData.ShipTypes shipType)
    {
        AddEnumBits(sensor, (int)shipType, ShipTypeBitCount);
    }

    private static void AddWeaponTypeBits(VectorSensor sensor, ConfigData.WeaponTypes weaponType)
    {
        AddEnumBits(sensor, (int)weaponType, WeaponTypeBitCount);
    }

    private static void AddEnumBits(VectorSensor sensor, int value, int bitCount)
    {
        for (int bit = 0; bit < bitCount; bit++)
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

    private void ResetAimDirections()
    {
        for (int i = 0; i < _lastAimDirections.Length; i++)
        {
            _lastAimDirections[i] = Vector2.up;
        }
    }
}
