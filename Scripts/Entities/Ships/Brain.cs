using System.Collections;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using System.Linq;
using System.Collections.Generic;
using Assets.Scripts.Levels;
using System;

namespace Assets.Scripts.Entities.Ships
{
    public class Brain : Agent
    {
        public Ship Ship, Enemy;
        public BufferSensorComponent BufferSensor;
        public float ShipType;
        public int SpottedShipIndex;
        public List<Ship> SpottedShips;
        public List<Ship> Ships;
        public float[] BlankObservation = new float[5];
        public List<SpottedShip> Filtered;
        public SpottedShip SpottedShip;
        public Ship LocalShip;
        public Vector2 NormalizedPosition;
        public bool Contains;
        public int SpottedShipCount, i, ss;

        public long Id;
        public void Setup(Ship ship)
        {
            Ship = ship;
            Id = Utilities.Hash();
            Ship.Level.AgentGroup.RegisterAgent(this);
            BufferSensor = gameObject.GetComponent<BufferSensorComponent>();
            ShipType = (float)Utilities.ShipTypeLetterToInt[Ship.ShipTypeLetter] / Utilities.ShipTypesAndTypeLetters.Count;
            SpottedShipIndex = Ship.Side - 1;
        }
        public override void Initialize()
        {
            //Debug.Log($"Initialized Agent #{Id}, belonging to {Ship}");

        }

        public override void OnEpisodeBegin()
        {
            //Academy.Instance.EnvironmentReset();

            if (Ship != null)
            {
                

                //Ship.Level.HasReset = true;
                //Ship.Level.ResetLevel(true);
                Ship.Body.velocity = Vector2.zero;
                Ship.transform.eulerAngles = Vector3.zero;
            }

        }

        /// <summary>
        /// Called when an action is received from either the player input or the neural network
        /// 
        /// vectorAction[i] represents:
        /// Index 0: The direction to move in
        /// </summary>
        /// <param name="vectorAction">The actions to take</param>
        public override void OnActionReceived(ActionBuffers vectorAction)
        {
            //Debug.Log($"An action buffer has been received for Agent #{Id}, belonging to {Ship}");
            if (Ship != null)
            {
                //Ship.transform.eulerAngles = new Vector3(0, 0, vectorAction.DiscreteActions[0] * 20);
                Ship.Direction = vectorAction.DiscreteActions[0] * 20;
                Ship.RLShootingStrategy = ConfigData.Configuration.ShootingStrategies.ElementAt(vectorAction.DiscreteActions[1]);
                //Debug.Log($"Chosen shooting strategy for {Ship.Name} is {Ship.ShootingStrategy}.");
                Ship.ShouldDetonate = vectorAction.DiscreteActions[2] == 1;

            }
            AddReward(-.0001f);
        }

        /// <summary>
        /// Collect vector observations from the environment
        /// </summary>
        /// <param name="sensor">The vector sensor</param>
        public override void CollectObservations(VectorSensor sensor)
        {
            //Debug.Log($"Collecting observations for Agent #{Id}, belonging to {Ship}");
            if (Ship == null)
            {
                sensor.AddObservation(BlankObservation);
                return;
            }


            // find the ships that this ship can see
            SpottedShips = Ship.Level.State.GetShips().Where((s) => s.DistanceTo(Ship) <= Ship.Sight && !s.Equals(Ship)).OrderBy((s) => s.DistanceTo(Ship)).ToList();
            Ships = SpottedShips;


            //Debug.Log($"{Ship.Name} has personally seen {spottedShips.Count} ships");

            // fill up the sensor with ships that have been spotted by allies
            if (SpottedShips.Count < BufferSensor.MaxNumObservables)
            {
                //remove spotted ships that this ship previously spotted or have died
                //state.SpottedShips[SpottedShipIndex] = state.SpottedShips[SpottedShipIndex].Where((s) => s.SpotterId != Ship.Id && !s.Ship.IsDead).ToList();
                Filtered = new List<SpottedShip>();
                for (i = 0; i < Ship.Level.State.SpottedShips[SpottedShipIndex].Count; i++)
                {
                    SpottedShip = Ship.Level.State.SpottedShips[SpottedShipIndex][i];
                    Contains = false;
                    SpottedShipCount = SpottedShips.Count;
                    for (ss = 0; ss < SpottedShipCount && !Contains; ss++)
                    {
                        Contains = SpottedShips[ss].Id == SpottedShip.Ship.Id;
                    }
                    if (!Contains && SpottedShip.SpotterId != Ship.Id && SpottedShip.Ship != null)
                    {
                        Filtered.Add(SpottedShip);
                        if (Ships.Count < BufferSensor.MaxNumObservables)
                        {
                            Ships.Add(SpottedShip.Ship);
                        }
                    }
                }

                Ship.Level.State.SpottedShips[SpottedShipIndex] = Filtered;
            }

            for (i = 0; i < Ships.Count; i++)
            {
                LocalShip = Ships[i];
                NormalizedPosition = LocalShip.GetPosition().normalized;
                BufferSensor.AppendObservation(new float[] { LocalShip.RLSide, NormalizedPosition.x, NormalizedPosition.y, LocalShip.RLHealth, LocalShip.RLShipType });
            }
            if (Ships.Count < BufferSensor.MaxNumObservables)
            {
                for (i = Ships.Count; i < BufferSensor.MaxNumObservables; i++)
                {
                    BufferSensor.AppendObservation(new float[] { 0, 0, 0, 0, 0 });
                }

            }


            // add the ships that this ship has personally seen to the central list
            Ship.Level.State.AddSpottedShips(SpottedShips, Ship);

            // Observe the agent's position (2 observations)
            sensor.AddObservation((Vector2)Ship.transform.position.normalized);

            // Observe the agent's health (1)
            sensor.AddObservation((float) Math.Round(((float)Ship.Health / Ship.MaxHealth), 2));

            // Observer the agent's ship type (1)
            sensor.AddObservation(ShipType);

        }

        /// <summary>
        /// When Behavior Type is set to "Heuristic Only" on the agent's Behavior Parameters,
        /// this function will be called. Its return values will be fed into
        /// <see cref="OnActionReceived(ActionBuffers vectorAction)"/> instead of using the nueral network
        /// </summary>
        /// <param name="actionsOut">An output action buffer</param>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;

            //Debug.Log($"Running heuristic for Agent #{Id}, belonging to {Ship}");
            if (Ship != null && Ship.HasTargetCoordinates)
            {
                float rotation = Ship.GetDegreesTowardsPoint(Ship.TargetCoordinates);
                int action = Mathf.RoundToInt(rotation/20);
                //Debug.Log($"Rotation: {rotation}, Action: {action}");
                discreteActions[0] = action;
            }
            else
            {
                discreteActions[0] = 360;
            }

        }
    }
}