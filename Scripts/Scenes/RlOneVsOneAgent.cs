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
/// Deliberately small ML-Agents adapter for the first Wasp-vs-Gunship proof. Four fixed Agent
/// instances cover both physical sides under both self-play team IDs, while exactly one Agent per
/// side is active in each duel. The team-to-side mapping alternates every game episode, so either
/// self-play team learns both ship types through one shared behavior/network.
/// </summary>
internal sealed class RlOneVsOneAgent : Agent
{
    internal const string BehaviorName = "BeesRL1v1";
    internal const int ObservationSize = 24;
    internal const int ContinuousActionCount = 5;
    internal const int DecisionPeriod = 5;

    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;

    private Stage _stage;
    private Ship _ship;
    private int _side;
    private int _teamId;
    private int _decisionCounter;
    private int _lastRewardedEpisode;
    private Vector2 _lastAimDirection = Vector2.up;

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
            Debug.LogError("RL 1v1 policy adapter could not find the training Stage.");
            return;
        }

        // AfterSceneLoad runs before the normal Scene readiness pump has necessarily loaded
        // ConfigData.Configuration. Wait for Stage finalization, which only occurs after settings and
        // user data are ready and after the Level setup path is safe to use.
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

        // Protect against duplicate installation if this bootstrap is invoked again during a
        // scene/domain lifecycle transition.
        if (stage.GetComponentsInChildren<RlOneVsOneAgent>(true).Length > 0)
        {
            yield break;
        }

        // Ship types remain on their authored factions. Instead, fixed ML-Agents team instances
        // alternate which faction they control each episode in RlOneVsOneEpisodeCoordinator.
        CreateAgent(stage, ConfigData.Configuration.BeeSide, 0, "Bee Team 0");
        CreateAgent(stage, ConfigData.Configuration.BeeSide, 1, "Bee Team 1");
        CreateAgent(stage, ConfigData.Configuration.HumanSide, 0, "Human Team 0");
        CreateAgent(stage, ConfigData.Configuration.HumanSide, 1, "Human Team 1");
    }

    private static void CreateAgent(Stage stage, int side, int teamId, string label)
    {
        GameObject agentObject = new GameObject($"RL 1v1 Agent - {label}");
        agentObject.transform.SetParent(stage.transform, false);

        BehaviorParameters behavior = agentObject.AddComponent<BehaviorParameters>();
        behavior.BehaviorName = BehaviorName;
        behavior.TeamId = teamId;
        behavior.BrainParameters.VectorObservationSize = ObservationSize;
        behavior.BrainParameters.NumStackedVectorObservations = 1;
        behavior.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(ContinuousActionCount);

        RlOneVsOneAgent agent = agentObject.AddComponent<RlOneVsOneAgent>();
        agent.Configure(stage, side, teamId);
    }

    private void Configure(Stage stage, int side, int teamId)
    {
        _stage = stage;
        _side = side;
        _teamId = teamId;
    }

    public override void Initialize()
    {
        RlOneVsOneEpisodeCoordinator.TsvRewardOccurred += HandleTsvRewardOccurred;
        RlOneVsOneEpisodeCoordinator.EpisodeEnded += HandleEpisodeEnded;
    }

    public override void OnEpisodeBegin()
    {
        ReleaseShip();
        _decisionCounter = 0;
        _lastAimDirection = Vector2.up;
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

        Level level = _ship.Level;
        float halfMap = RlOneVsOneTrainingBootstrap.TrainingMapSize / 2f;
        Vector2 shipPosition = _ship.GetPosition();

        // Self: type, map-local position, heading, whether it is moving, and health. Speed itself
        // is fixed by ship type in this proof, so X/Y velocity would duplicate type + heading.
        sensor.AddObservation(GetShipTypeIndicator(_ship));
        sensor.AddObservation(shipPosition.x / halfMap);
        sensor.AddObservation(shipPosition.y / halfMap);
        AddHeading(sensor, _ship.Rotation);
        sensor.AddObservation(_ship.IsMoving ? 1f : 0f);
        sensor.AddObservation(GetHealthFraction(_ship));

        Ship enemy = FindVisibleEnemy(level);
        if (enemy == null)
        {
            AddZeroObservations(sensor, 8);
        }
        else
        {
            // Enemy information comes only from the side's Hive Mind memory, never an omniscient
            // GetAllEnemyShips/GetShips lookup. In this 1v1 proof there can be at most one enemy.
            Vector2 relativePosition = enemy.GetPosition() - shipPosition;

            sensor.AddObservation(1f);
            sensor.AddObservation(relativePosition.x / RlOneVsOneTrainingBootstrap.TrainingMapSize);
            sensor.AddObservation(relativePosition.y / RlOneVsOneTrainingBootstrap.TrainingMapSize);
            AddHeading(sensor, enemy.Rotation);
            sensor.AddObservation(enemy.IsMoving ? 1f : 0f);
            sensor.AddObservation(GetHealthFraction(enemy));
            sensor.AddObservation(GetShipTypeIndicator(enemy));
        }

        Turret turret = GetPrimaryTurret();
        if (turret == null)
        {
            AddZeroObservations(sensor, 9);
            return;
        }

        // Weapon: mounting point, facing, range, damage, and firing state. The trainer has vector
        // observation normalization enabled, so raw authored power remains useful across ship types
        // without hard-coding a maximum weapon power into this adapter.
        Vector2 relativeWeaponPosition = turret.GetPosition() - shipPosition;
        float shipSizeScale = Mathf.Max(1f, _ship.LongestSide);
        sensor.AddObservation(relativeWeaponPosition.x / shipSizeScale);
        sensor.AddObservation(relativeWeaponPosition.y / shipSizeScale);
        AddHeading(sensor, turret.Rotation);
        sensor.AddObservation((float)turret.Range / RlOneVsOneTrainingBootstrap.TrainingMapSize);
        sensor.AddObservation((float)turret.Power);
        sensor.AddObservation(turret.RateOfFire / (1f + Mathf.Max(0f, turret.RateOfFire)));
        sensor.AddObservation(turret.PassesPerFire > 0
            ? Mathf.Clamp01((float)turret.TargetingPasses / turret.PassesPerFire)
            : 0f);
        sensor.AddObservation(turret.IsAimedAtTarget ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!IsCurrentController() || !TryBindShip())
        {
            return;
        }

        var continuous = actions.ContinuousActions;
        Vector2 movement = new Vector2(continuous[0], continuous[1]);
        if (movement.sqrMagnitude < MovementDeadZone * MovementDeadZone)
        {
            _ship.Direction = 360;
        }
        else
        {
            Vector2 movementPoint = _ship.GetPosition() + movement.normalized;
            int direction = Mathf.RoundToInt(_ship.GetDegreesTowardsPoint(movementPoint));
            _ship.Direction = ((direction % 360) + 360) % 360;
        }
        _ship.HasBrain = true;

        Vector2 aim = new Vector2(continuous[2], continuous[3]);
        if (aim.sqrMagnitude >= AimDeadZone * AimDeadZone)
        {
            _lastAimDirection = aim.normalized;
        }

        bool fireRequested = continuous[4] > 0f;
        for (int i = 0; i < _ship.Turrets.Count; i++)
        {
            Turret turret = _ship.Turrets[i];
            Vector2 targetPoint = turret.GetPosition() + _lastAimDirection * Mathf.Max(1f, turret.Range);
            turret.SetRlControl(targetPoint, fireRequested);
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // With no Python trainer connected, pressing Play still produces visibly random actions for
        // a quick environment/control smoke test. Training overrides this through BehaviorName.
        var continuous = actionsOut.ContinuousActions;
        continuous[0] = Random.Range(-1f, 1f);
        continuous[1] = Random.Range(-1f, 1f);
        continuous[2] = Random.Range(-1f, 1f);
        continuous[3] = Random.Range(-1f, 1f);
        continuous[4] = Random.value >= 0.5f ? 1f : -1f;
    }

    private void HandleTsvRewardOccurred(int side, float reward)
    {
        if (side == _side && IsCurrentController())
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

        int assignedTeamId = _side == ConfigData.Configuration.BeeSide
            ? result.BeeTeamId
            : result.HumanTeamId;
        _lastRewardedEpisode = result.EpisodeNumber;
        if (_teamId != assignedTeamId)
        {
            // This fixed Agent was idle for this physical duel. Ending it would create a synthetic
            // zero-step trajectory, so leave its fresh internal episode untouched until it next owns a side.
            return;
        }

        // TSV shaping was already delivered at each hit. Add only the terminal outcome and
        // winner-only time preference here so the same TSV exchange is never counted twice.
        float reward = _side == ConfigData.Configuration.BeeSide
            ? result.BeeTerminalReward + result.BeeTimeReward
            : result.HumanTerminalReward + result.HumanTimeReward;
        AddReward(reward);
        EndEpisode();
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

        List<Ship> ships = level.State.GetShips(_side);
        Ship current = null;
        for (int i = 0; i < ships.Count; i++)
        {
            if (ships[i] != null && !ships[i].IsDead)
            {
                current = ships[i];
                break;
            }
        }

        if (current == _ship)
        {
            return current != null;
        }

        ReleaseShip();
        _ship = current;
        if (_ship == null)
        {
            return false;
        }

        // This scene has no human/player controller. Make the runtime squad state agree even if the
        // account-level DoesUserHaveController setting would otherwise make Level.HasPlayer true.
        if (_ship.Squad != null)
        {
            _ship.Squad.IsUserControlled = false;
            _ship.Squad.IsHiveMindControlled = true;
            _ship.Squad.CanAcceptUserInput = false;
        }

        _ship.HasBrain = true;
        if (_ship.Turrets.Count == 0)
        {
            Debug.LogError($"RL 1v1 expected {_ship.ShipType} to have at least one turret-controlled weapon.");
            return false;
        }

        for (int i = 0; i < _ship.Turrets.Count; i++)
        {
            Turret turret = _ship.Turrets[i];
            turret.SetRlControl(turret.GetPosition() + Vector2.up * Mathf.Max(1f, turret.Range), false);
        }
        return true;
    }

    private void ReleaseShip()
    {
        if (_ship == null)
        {
            return;
        }

        _ship.HasBrain = false;
        for (int i = 0; i < _ship.Turrets.Count; i++)
        {
            _ship.Turrets[i].ClearRlControl();
        }
        _ship = null;
    }

    private Ship FindVisibleEnemy(Level level)
    {
        foreach (Ship candidate in level.State.GetShipsVisibleToHiveMind(_side))
        {
            if (candidate != null && !candidate.IsDead && candidate.Side != _side)
            {
                return candidate;
            }
        }
        return null;
    }

    private Turret GetPrimaryTurret()
    {
        return _ship != null && _ship.Turrets.Count > 0 ? _ship.Turrets[0] : null;
    }

    private static float GetHealthFraction(Ship ship)
    {
        return ship != null && ship.MaxHealth > 0
            ? Mathf.Clamp01((float)ship.Health / ship.MaxHealth)
            : 0f;
    }

    private static float GetShipTypeIndicator(Ship ship)
    {
        if (ship == null)
        {
            return 0f;
        }
        if (ship.ShipType == RlOneVsOneTrainingBootstrap.BeeShipType)
        {
            return -1f;
        }
        if (ship.ShipType == RlOneVsOneTrainingBootstrap.HumanShipType)
        {
            return 1f;
        }
        return 0f;
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
