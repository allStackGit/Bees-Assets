using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

/// <summary>
/// Deliberately small ML-Agents adapter for the first Wasp-vs-Gunship proof. Two instances use the
/// same behavior name with opposing team IDs, so one shared policy learns to control either ship.
/// This is not the eventual variable-fleet observation architecture.
/// </summary>
internal sealed class RlOneVsOneAgent : Agent
{
    internal const string BehaviorName = "BeesRL1v1";
    internal const int ObservationSize = 25;
    internal const int ContinuousActionCount = 5;
    internal const int DecisionPeriod = 5;

    private const float MovementDeadZone = 0.2f;
    private const float AimDeadZone = 0.1f;

    private Stage _stage;
    private Ship _ship;
    private int _side;
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

        CreateAgent(stage, ConfigData.Configuration.BeeSide, 0, "Bee");
        CreateAgent(stage, ConfigData.Configuration.HumanSide, 1, "Human");
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
        agent.Configure(stage, side);
    }

    private void Configure(Stage stage, int side)
    {
        _stage = stage;
        _side = side;
    }

    public override void Initialize()
    {
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
        if (_stage == null || !_stage.IsTrainingNueralNetwork || !TryBindShip())
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
        if (!TryBindShip())
        {
            AddZeroObservations(sensor, ObservationSize);
            return;
        }

        Level level = _ship.Level;
        float halfMap = RlOneVsOneTrainingBootstrap.TrainingMapSize / 2f;
        Vector2 shipPosition = _ship.GetPosition();
        Vector2 shipVelocity = GetVelocity(_ship);

        // Self: type, map-local position, heading, velocity and health.
        sensor.AddObservation(GetShipTypeIndicator(_ship));
        sensor.AddObservation(shipPosition.x / halfMap);
        sensor.AddObservation(shipPosition.y / halfMap);
        AddHeading(sensor, _ship.Rotation);
        float ownSpeedScale = Mathf.Max(1f, _ship.Speed);
        sensor.AddObservation(shipVelocity.x / ownSpeedScale);
        sensor.AddObservation(shipVelocity.y / ownSpeedScale);
        sensor.AddObservation(GetHealthFraction(_ship));

        Ship enemy = FindVisibleEnemy(level);
        if (enemy == null)
        {
            AddZeroObservations(sensor, 9);
        }
        else
        {
            // Enemy information comes only from the side's Hive Mind memory, never an omniscient
            // GetAllEnemyShips/GetShips lookup. In this 1v1 proof there can be at most one enemy.
            Vector2 enemyVelocity = GetVelocity(enemy);
            Vector2 relativePosition = enemy.GetPosition() - shipPosition;
            Vector2 relativeVelocity = enemyVelocity - shipVelocity;
            float relativeSpeedScale = Mathf.Max(1f, _ship.Speed + enemy.Speed);

            sensor.AddObservation(1f);
            sensor.AddObservation(relativePosition.x / RlOneVsOneTrainingBootstrap.TrainingMapSize);
            sensor.AddObservation(relativePosition.y / RlOneVsOneTrainingBootstrap.TrainingMapSize);
            AddHeading(sensor, enemy.Rotation);
            sensor.AddObservation(relativeVelocity.x / relativeSpeedScale);
            sensor.AddObservation(relativeVelocity.y / relativeSpeedScale);
            sensor.AddObservation(GetHealthFraction(enemy));
            sensor.AddObservation(GetShipTypeIndicator(enemy));
        }

        Turret turret = GetPrimaryTurret();
        if (turret == null)
        {
            AddZeroObservations(sensor, 8);
            return;
        }

        // Weapon: mounting point relative to the hull, current facing and the state needed to learn
        // aiming/firing without bypassing authored range or rate-of-fire mechanics.
        Vector2 relativeWeaponPosition = turret.GetPosition() - shipPosition;
        float shipSizeScale = Mathf.Max(1f, _ship.LongestSide);
        sensor.AddObservation(relativeWeaponPosition.x / shipSizeScale);
        sensor.AddObservation(relativeWeaponPosition.y / shipSizeScale);
        AddHeading(sensor, turret.Rotation);
        sensor.AddObservation((float)turret.Range / RlOneVsOneTrainingBootstrap.TrainingMapSize);
        sensor.AddObservation(turret.RateOfFire / (1f + Mathf.Max(0f, turret.RateOfFire)));
        sensor.AddObservation(turret.PassesPerFire > 0
            ? Mathf.Clamp01((float)turret.TargetingPasses / turret.PassesPerFire)
            : 0f);
        sensor.AddObservation(turret.IsAimedAtTarget ? 1f : 0f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!TryBindShip())
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

    private void HandleEpisodeEnded(RlOneVsOneEpisodeCoordinator.EpisodeResult result)
    {
        if (result.EpisodeNumber <= _lastRewardedEpisode)
        {
            return;
        }

        float reward = _side == ConfigData.Configuration.BeeSide
            ? result.BeeTotalReward
            : result.HumanTotalReward;
        _lastRewardedEpisode = result.EpisodeNumber;
        AddReward(reward);
        EndEpisode();
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

    private static Vector2 GetVelocity(Ship ship)
    {
        return ship != null && ship.Body != null ? ship.Body.linearVelocity : Vector2.zero;
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
