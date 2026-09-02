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
/// Fixed-shape shared combat policy adapter. Curriculum changes fill or clear reserved observation
/// and action slots instead of changing the neural-network contract.
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
    private int _decisionCounter;
    private int _lastRewardedEpisode;
    private bool _hasBoundShip;
    private bool _hasParticipatedThisEpisode;
    private long _boundRuntimeShipId;
    private readonly Vector2[] _lastAimDirections = new Vector2[MaxWeaponSlots];
    private readonly List<Ship> _bindCandidates = new List<Ship>();
    private readonly List<Ship> _allyCandidates = new List<Ship>();
    private readonly List<Ship> _enemyCandidates = new List<Ship>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallForDedicatedTrainingScene()
    {
        if (!RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime) return;
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
        while (stage != null && (!stage.IsFinalized || ConfigData.Configuration == null)) yield return null;
        if (stage == null || !RlOneVsOneTrainingBootstrap.IsDedicatedTrainingRuntime) yield break;
        if (stage.GetComponentsInChildren<RlOneVsOneAgent>(true).Length > 0) yield break;

        // Reserve wrappers for starting ships plus episode-spawned children. Idle wrappers do not
        // request decisions and never create a trajectory.
        for (int slot = 0; slot < MaxControlledShipsPerSide; slot++)
        {
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 0, $"Bee Team 0 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.BeeSide, 1, $"Bee Team 1 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 0, $"Human Team 0 Slot {slot}");
            CreateAgent(stage, ConfigData.Configuration.HumanSide, 1, $"Human Team 1 Slot {slot}");
        }

        Debug.Log($"RL combat policy schema observations={ObservationSize} actions={ContinuousActionCount} " +
                  $"allies={MaxObservedAllies} enemies={MaxObservedEnemies} weapon_slots={MaxWeaponSlots} " +
                  $"control_slots_per_side={MaxControlledShipsPerSide}");
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
        behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ContinuousActionCount);
        RlOneVsOneAgent agent = obj.AddComponent<RlOneVsOneAgent>();
        agent._stage = stage;
        agent._side = side;
        agent._teamId = teamId;
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
        if (_stage == null || !_stage.IsTrainingNueralNetwork || !IsCurrentController() || !TryBindShip()) return;
        if (++_decisionCounter >= DecisionPeriod)
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
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!IsCurrentController() || !TryBindShip()) return;
        var values = actions.ContinuousActions;
        ApplyMovement(new Vector2(values[0], values[1]));

        if (_ship.Weapons != null)
        {
            for (int i = 0; i < _ship.Weapons.Count; i++)
            {
                if (!(_ship.Weapons[i] is Turret turret)) continue;
                int slot = Mathf.Min(i, MaxWeaponSlots - 1);
                int action = MovementActionCount + slot * ActionsPerWeaponSlot;
                Vector2 aim = new Vector2(values[action], values[action + 1]);
                if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone) _lastAimDirections[slot] = aim.normalized;
                Vector2 target = turret.GetPosition() + _lastAimDirections[slot] * Mathf.Max(1f, turret.Range);
                turret.SetRlControl(target, values[action + 2] > 0f);
            }
        }

        int special = MovementActionCount + MaxWeaponSlots * ActionsPerWeaponSlot;
        if (values[special] > 0f) ApplySpecialAction();
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var values = actionsOut.ContinuousActions;
        for (int i = 0; i < ContinuousActionCount; i++) values[i] = Random.Range(-1f, 1f);
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

    private void ApplySpecialAction()
    {
        if (_ship is YellowJacket yellowJacket) yellowJacket.TryToDetonate();
        else if (_ship is Striker striker) striker.TryToDropBombs();
        else if (_ship is FireBarge fireBarge) fireBarge.Detonate();
        else if (_ship is Barge barge && !barge.HasStartedCharging && !barge.IsCharging)
            barge.StartCoroutine(barge.ChargeForward(FindNearestVisibleEnemy()));
    }

    private void HandleTsvRewardOccurred(int side, float reward)
    {
        if (side == _side && IsCurrentController() && _hasParticipatedThisEpisode) AddReward(reward);
    }

    private void HandleEpisodeEnded(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (result.EpisodeNumber <= _lastRewardedEpisode) return;
        _lastRewardedEpisode = result.EpisodeNumber;
        int assignedTeam = _side == ConfigData.Configuration.BeeSide ? result.BeeTeamId : result.HumanTeamId;
        if (_teamId != assignedTeam || !_hasParticipatedThisEpisode) return;

        AddReward(_side == ConfigData.Configuration.BeeSide
            ? result.BeeTerminalReward + result.BeeTimeReward
            : result.HumanTerminalReward + result.HumanTimeReward);
        if (result.TimedOut) EpisodeInterrupted(); else EndEpisode();
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
            if (_ship != null && !_ship.IsDead && _ship.Level == level && _ship.Id == _boundRuntimeShipId) return true;
            _ship = null; // A dead participant never jumps to a surviving ship.
            return false;
        }

        _bindCandidates.Clear();
        List<Ship> ships = level.State.GetShips(_side);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship candidate = ships[i];
            if (candidate != null && !candidate.IsDead && !IsControlledByAnotherAgent(candidate)) _bindCandidates.Add(candidate);
        }
        if (_bindCandidates.Count == 0) return false;
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
            if (other != null && other != this && other._side == _side && other._teamId == _teamId &&
                other._hasBoundShip && other._ship == candidate) return true;
        }
        return false;
    }

    private static int CompareShipsForControl(Ship left, Ship right)
    {
        long leftFleet = left != null && left.FleetShip != null ? left.FleetShip.Id : long.MaxValue;
        long rightFleet = right != null && right.FleetShip != null ? right.FleetShip.Id : long.MaxValue;
        int compare = leftFleet.CompareTo(rightFleet);
        if (compare != 0) return compare;
        long leftRuntime = left != null ? left.Id : long.MaxValue;
        long rightRuntime = right != null ? right.Id : long.MaxValue;
        return leftRuntime.CompareTo(rightRuntime);
    }

    private bool ValidateShipFitsArena(Ship ship)
    {
        float extent = Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight());
        if (RlOneVsOneTrainingBootstrap.CurrentMapSize > extent * 2f) return true;
        if (!_invalidEnvironmentReported)
        {
            _invalidEnvironmentReported = true;
            Debug.LogError($"RL arena size {RlOneVsOneTrainingBootstrap.CurrentMapSize:0.###} cannot contain " +
                           $"{ship.ShipType} (diameter {extent * 2f:0.###}).");
            if (_stage != null) _stage.IsTrainingNueralNetwork = false;
            if (!Application.isEditor) Application.Quit(3);
        }
        return false;
    }

    private void ReleaseShip()
    {
        if (_ship != null)
        {
            _ship.HasBrain = false;
            for (int i = 0; i < _ship.Turrets.Count; i++) _ship.Turrets[i].ClearRlControl();
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
            if (candidate != null && candidate != _ship && !candidate.IsDead) _allyCandidates.Add(candidate);
        }
        SortByDistanceThenId(_allyCandidates, origin);
    }

    private void CollectVisibleEnemies(Vector2 origin)
    {
        _enemyCandidates.Clear();
        foreach (Ship candidate in _ship.Level.State.GetShipsVisibleToHiveMind(_side))
        {
            if (candidate != null && !candidate.IsDead && candidate.Side != _side) _enemyCandidates.Add(candidate);
        }
        SortByDistanceThenId(_enemyCandidates, origin);
    }

    private static void SortByDistanceThenId(List<Ship> ships, Vector2 origin)
    {
        ships.Sort((left, right) =>
        {
            int compare = (left.GetPosition() - origin).sqrMagnitude.CompareTo((right.GetPosition() - origin).sqrMagnitude);
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

    private Ship FindNearestVisibleEnemy()
    {
        if (_ship == null) return null;
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
            return yellowJacket.TouchingShip != null && !yellowJacket.TouchingShip.IsDead &&
                   yellowJacket.TouchingShip.Side != yellowJacket.Side ? 1f : 0f;
        if (ship is Striker striker) return striker.IsBombReady ? 1f : 0f;
        if (ship is Barge barge) return !barge.HasStartedCharging && !barge.IsCharging ? 1f : 0f;
        if (ship is FireBarge) return 1f;
        return 0f;
    }

    private static float GetHealthFraction(Ship ship)
    {
        return ship != null && ship.MaxHealth > 0 ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth) : 0f;
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
        for (int bit = 0; bit < bits; bit++) sensor.AddObservation((value & (1 << bit)) != 0 ? 1f : 0f);
    }

    private static void AddHeading(VectorSensor sensor, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        sensor.AddObservation(Mathf.Sin(radians));
        sensor.AddObservation(Mathf.Cos(radians));
    }

    private static void AddZeroObservations(VectorSensor sensor, int count)
    {
        for (int i = 0; i < count; i++) sensor.AddObservation(0f);
    }

    private void ResetAimDirections()
    {
        for (int i = 0; i < _lastAimDirections.Length; i++) _lastAimDirections[i] = Vector2.up;
    }
}
