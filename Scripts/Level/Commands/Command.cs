using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;
using Microsoft.CSharp;

using Assets.Scripts;
using UnityEngine.UIElements;
using Assets.Scripts.Entities;
using Assets.Scripts.Scenes;
using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Level.Commands
{
    public class Command : MonoBehaviour
    {
        public long Age, Tsv;      
        public long OutcomeId = 0; 
        public Squad EnemySquad, Squad;
        public string Matchup, FinalizationCause;
        public Strategy Strategy = null;
        public MatchupStrategy MatchupStrategy = null;
        public ShootingStrategy ShootingStrategy = null;
        public bool IsAttacking, IsCloseToTarget;
        public float CommandFrequency = 3;
        /// <summary>
        /// The targeting queue, unmodified from when it was generated, only regenerated when a new ship is added to the enemy squad
        /// </summary>
        public Queue<Ship> OriginalQueue;
        /// <summary>
        /// The list of ships (in order) that this squad's ships should follow after, modified each time a ship takes an enemy ship off the queue and follows it
        /// </summary>
        public Queue<Ship> TargetingQueue;


        private List<Vector2> _destinations = new List<Vector2>();

        public bool HasStrategy => Strategy != null;
        public bool HasShootingStrategy => ShootingStrategy != null;
        public bool HasSquad => Squad != null;
        public bool HasEnemy => EnemySquad != null;
        public string Type => HasStrategy ? Strategy.Name : "Uninitialized";
        public bool IsFinalized, IsStored, IsHiveMindCommand;
        public bool HasDestination => _destinations.Count > 0;
        public LevelStage Level => Squad.Level;
        public int Side => Squad.Side;


        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup)
        {
            Squad = squad;
            EnemySquad = enemy;
            Matchup = matchup;
            IsHiveMindCommand = isHiveMindCommand;

            if (EnemySquad != null)
            {
                OriginalQueue = new Queue<Ship>(MakeTargetingQueue());
                TargetingQueue = new Queue<Ship>(OriginalQueue);
            }
        }

        public void SetDestination(Vector2 destination)
        {
            ClearDestinations();
            AddDestination(destination);
        }
        public List<Vector2> GetDestinations()
        {
            return _destinations;
        }
        public void AddDestination(Vector2 destination)
        {
            //float x = Mathf.Clamp(destination.x, Level.MinX, Level.MaxX);
            //float y = Mathf.Clamp(destination.y, Level.MinY, Level.MaxY);

            //destination = new Vector2(x, y);

            _destinations.Add(destination); 
        }
        public void ClearDestinations()
        {
            _destinations.Clear();
        }
        public void RemoveDestination(Vector2 destination)
        {
            _destinations.Remove(destination);
        }
        public Vector2 GetDestination()
        {
            return GetDestinations().FirstOrDefault();
        }
        public void SetAndMove(Vector2 destination)
        {
            SetDestination(destination);
            Squad.Move(GetDestination());
        }
        public void MoveTowardsEnemies()
        {
            Squad.GetShips().ForEach((ship) =>
            {
                ship.MoveToPoint(ship.SetAndGetTargetEnemy().GetPosition());

            });
        }
        protected void Timeout()
        {
            SetFinalize("The command ran out of time");
        }
        public void StandStill()
        {
            if (Squad!= null)
            {
                ClearDestinations();
                SetAndMove(Squad.GetPosition());
            }

        }

        public virtual void Execute(Strategy strategy, ShootingStrategy shootingStrategy, long commandOutcomeId, bool noEnemy)
        {
            if (noEnemy || EnemySquad != null)
            {
                Strategy = strategy;
                ShootingStrategy = shootingStrategy;
                Squad.ClearTargets(); // Clear all old targets before starting the new command

                OutcomeId = commandOutcomeId;
                if (!Level.IsTraining)
                {
                    Squad.PastCommands.Add(new StoredCommand(this));
                }
                Level.GetState().AddCommand(this);

                Squad.Status = $"Executing Command #{OutcomeId}";
                //Debug.Log("Set status for command");
            }
            else
            {
                //Debug.Log($"Could not find the enemy for command #{commandOutcomeId}");
                SetFinalize("Could not find the enemy squad for command");
                return;
            }

        }

        public List<Ship> MakeTargetingQueue()
        {

            List<Ship> queue = EnemySquad.GetShips();
            string strategy = Squad.GetShootingStrategy();
            if (strategy != null)
            {
                //Debug.Log($"Making targeting queue for {Ship.Name}. The squad is using {Squad.GetShootingStrategy()}");
                switch (strategy)
                {
                    case "First Seen":
                        return queue;
                    case "Random":
                        queue.Sort((a, b) => Utilities.RandomSign());
                        break;
                        return queue.OrderBy(s => Utilities.RandomInt(2)).ToList();
                    case "Revenge":
                        queue.Sort((a, b) => b.LastKilled - a.LastKilled);
                        break;
                        return queue.OrderByDescending(s => s.LastKilled).ToList();
                    case "Most Dangerous":
                        queue.Sort((a, b) => b.FleetShip.DamageDone - a.FleetShip.DamageDone);
                        break;
                        return queue.OrderByDescending(s => s.FleetShip.DamageDone).ToList();
                    case "Least Health":
                        queue.Sort((a, b) => a.Health - b.Health);
                        break;
                        return queue.OrderBy(s => s.Health).ToList();
                    case "Most Health":
                        queue.Sort((a, b) => b.Health - a.Health);
                        break;
                        return queue.OrderByDescending(s => s.Health).ToList();
                    case "Most Powerful":
                        queue.Sort((a, b) => (int) (b.Firepower - a.Firepower));
                        break;
                        return queue.OrderByDescending(s => s.Firepower).ToList();
                    case "Least Powerful":
                        queue.Sort((a, b) => (int) (a.Firepower - b.Firepower));
                        break;
                        return queue.OrderBy(s => s.Firepower).ToList();
                    case "Closest":
                        queue.Sort((a, b) => (int)(Squad.DistanceToPoint(a.GetPosition()) - Squad.DistanceToPoint(b.GetPosition())));
                        break;
                        return queue.ToList();
                    case "Furthest":
                        queue.Sort((a, b) => (int)(Squad.DistanceToPoint(b.GetPosition()) - Squad.DistanceToPoint(a.GetPosition())));
                        break;
                        return queue.ToList();
                    case "Most Range":
                        queue.Sort((a, b) => b.MaxRange - a.MaxRange);
                        break;
                        return queue.OrderByDescending(s => s.MaxRange).ToList();
                    case "Least Range":
                        queue.Sort((a, b) => a.MaxRange - b.MaxRange);
                        break;
                        return queue.OrderBy(s => s.MaxRange).ToList();
                    case "Fastest":
                        queue.Sort((a, b) => (int) (b.Speed - a.Speed));
                        break;
                        return queue.OrderByDescending(s => s.Speed).ToList();
                    case "Slowest":
                        queue.Sort((a, b) => (int)(a.Speed - b.Speed));
                        break;
                        return queue.OrderBy(s => s.Speed).ToList();
                    case "Most Valuable":
                        queue.Sort((a, b) => b.Tsv - a.Tsv);
                        break;
                        return queue.OrderByDescending(s => s.Tsv).ToList();
                    case "Least Valuable":
                        queue.Sort((a, b) => a.Tsv - b.Tsv);
                        break;
                        return queue.OrderBy(s => s.Tsv).ToList();
                    default:
                        if (strategy.StartsWith("Type "))
                        {
                            string type = strategy.Substring(5);
                            queue.Sort((a, b) =>
                            {
                                //Debug.Log($"Strategy: {strategy}, Type: {type}, A ShipTypeLetter: {a.ShipTypeLetter}, B ShipTypeLetter: {b.ShipTypeLetter}");
                                if (a.ShipTypeLetter == type && b.ShipTypeLetter != type)
                                {
                                    return -1;
                                }
                                else if (b.ShipTypeLetter == type && a.ShipTypeLetter != type)
                                {
                                    return 1;
                                }
                                else
                                {
                                    return 0;
                                }
                            });
                            //if (queue.Count > 0)
                            //{
                            //    Debug.Log($"The first entry in the sorted queue is {queue.First().Name}");
                            //}
                            return queue;
                        }
                        else
                        {
                            return queue;
                        }
                }
            }
            return queue;
        }
        /*
         * This method finds the enemies of the command's squad and makes sure there's a ship damage status entry for each enemy ship
         */
        public void PrepareDamageToSendEntries(string which = "")
        {
            if (!Squad.IsDefenseless)
            {
                List<Ship> ships = new List<Ship>();
                if (which == "closest")
                {
                    Squad closestEnemy = Squad.GetClosestEnemySquad();
                    if (closestEnemy != null)
                    {
                        ships = closestEnemy.GetShips();

                    }
                }
                else if (EnemySquad != null)
                {
                    ships = EnemySquad.GetShips();
                }
                foreach (Ship ship in ships)
                {
                    Level.GetState().GetShipDamageStatus(Side, ship);
                }
            }
        }

        // Finalizes the command and stops the squad so long as the command hasn't already been finalized
        public virtual void SetFinalize(string cause)
        {
            if (!Squad.IsDead)
            {
                //StandStill();
                //Squad.StopMoving("Command ended");
                Finalize(cause);
            }

        }
        public void SquadKilled()
        {
            if (Type == "Mining")
            {
                ((Mining)this).CleanupAsteroid();
            }else if (Type == "Full Retreat")
            {
                ((FullRetreat)this).CleanupWarpGate();
            }
            Finalize("This squad got killed");
        }
        private void Finalize(string cause)
        {
            CancelInvoke();
            StopAllCoroutines();

            FinalizationCause = cause;
            //Debug.Log($"Finalized #{OutcomeId} - {Strategy.Name} because of [{FinalizationCause}]");
            IsFinalized = true;
            //ClearDestinations();

            //if (Squad != null)
            //{
                
            //}
            //else
            //{
            //    Debug.LogError($"Tried to finalize command #{OutcomeId} for a null squad");
            //}

            Squad.GetShips().ForEach((ship) =>
            {
                ship.TargetEnemyShipToFollow = null;

            });
            //Squad.IsRetreating = false;
            Squad.Status = "idle";
            if (Squad.IsChasing())
            {
                Squad.SetChase(false);
            }
            if (Squad.IsSelected && Level.Menus != null)
            {
                Level.Menus.ActionBox.HighlightSelectedButtons();
            }

            Squad.Command = null;

            if (Squad.IsHiveMindControlled && Level.ActivateHiveMind)
            {
                Squad.AddToCommandList();
            }
            if (Level.IsDebugging)
            {
                StoredCommand storedCommand = Level.GetState().GetPastCommands().FirstOrDefault(c => c.OutcomeId == OutcomeId);
                if (storedCommand != null)
                {
                    if (storedCommand.IsStored)
                    {
                        Debug.Log($"Trying to finalize a command #${OutcomeId} with cause [{cause}] that has already been stored");
                        return;
                    }
                    storedCommand.Tsv = Tsv;
                    storedCommand.IsFinalized = true;
                    //storedCommand.FinalizationCause = cause;

                    StoredCommand squadCommand = Squad.PastCommands.FirstOrDefault(c => c.OutcomeId == OutcomeId);
                    if (squadCommand != null)
                    {
                        //squadCommand.Age = Age;
                        if (EnemySquad != null)
                        {
                            squadCommand.Enemy = EnemySquad.Name;
                        }
                        else
                        {
                            squadCommand.Enemy = "";
                        }
                        squadCommand.Tsv = Tsv;
                        squadCommand.FinalizationCause = cause;
                    }

                }
                else
                {
                    Debug.Log($"Couldn't find a past command with id #{OutcomeId}  with cause [{cause}]");
                }
            }
            // 


            //Debug.Log($"Trying to destroy ({Squad.gameObject.name}, {name}) {Squad.gameObject.GetComponent<Command>()}");
            //if (Squad != null)
            //{
            //    Destroy(Squad.gameObject.GetComponents<Command>().First((c) => c.OutcomeId == OutcomeId));
            //}
            Destroy(this);
        }

        public override string ToString()
        {
            return $"Command #{(OutcomeId != 0 ? OutcomeId : "N/A")} with Strategy {(HasStrategy ? Strategy.Name : "N/A")} attached to " +
                $"Squad #{Squad.Id} - {Squad.Name}";
        }
        
    }
}
