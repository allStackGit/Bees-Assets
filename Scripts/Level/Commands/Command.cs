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
        public Squad Enemy, Squad;
        public string Matchup, FinalizationCause;
        public Strategy Strategy = null;
        public MatchupStrategy MatchupStrategy = null;
        public ShootingStrategy ShootingStrategy = null;
        public bool IsAttacking;


        private List<Vector2> _destinations = new List<Vector2>();

        public bool HasStrategy => Strategy != null;
        public bool HasShootingStrategy => ShootingStrategy != null;
        public bool HasSquad => Squad != null;
        public bool HasEnemy => Enemy != null;
        public string Type => HasStrategy ? Strategy.Name : "Uninitialized";
        public bool IsFinalized, IsStored, IsHiveMindCommand;
        public LevelStage Level => Squad.Level;
        public int Side => Squad.Side;


        public void Setup(Squad squad, bool isHiveMindCommand, Squad enemy, string matchup)
        {
            Squad = squad;
            Enemy = enemy;
            Matchup = matchup;
            IsHiveMindCommand = isHiveMindCommand;
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
            if (noEnemy || Enemy != null)
            {
                Strategy = strategy;
                ShootingStrategy = shootingStrategy;

                OutcomeId = commandOutcomeId;
                Squad.PastCommands.Add(new StoredCommand(this));
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
        /*
         * This method finds the enemies of the command's squad and makes sure there's a ship damage status entry for each enemy ship
         */
        public void PrepareDamageToSendEntries(string which = "")
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
            else if (Enemy != null)
            {
                ships = Enemy.GetShips();
            }
            foreach(Ship ship in ships)
            {
                ShipDamageStatus entry = Squad.DamageSentToEnemyShipsBySquad.Find((entry) => entry.ship.Equals(ship));
                if (entry == null)
                {
                    Squad.DamageSentToEnemyShipsBySquad.Add(new ShipDamageStatus(ship));
                }
            }
            
        }

        // Finalizes the command and stops the squad so long as the command hasn't already been finalized
        public void SetFinalize(string cause)
        {
            if (!Squad.IsDead)
            {
                StandStill();
                Finalize(cause);
            }

        }
        public void SquadKilled()
        {
            Finalize("This squad got killed");
        }
        private void Finalize(string cause)
        {
            CancelInvoke(); 
            
            FinalizationCause = cause;
            //Debug.Log($"Finalized because of [{FinalizationCause}]");
            IsFinalized = true;
            ClearDestinations();

            if (Squad != null)
            {
                Squad.IsRetreating = false;
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
                    if (OutcomeId != 0 && IsHiveMindCommand)
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
                            squadCommand.Age = Age;
                            squadCommand.Enemy = Enemy.Name;
                            squadCommand.Tsv = Tsv;
                            squadCommand.FinalizationCause = cause;
                        }
                        else
                        {
                            Debug.Log($"Couldn't find a past command with id #{OutcomeId}  with cause [{cause}]");
                        }
                    }
                }
            }
            else
            {
                Debug.Log($"Tried to finalize command #{OutcomeId} for a null squad");
            }
            // 

            
            this.Level.GetState().LogState();
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
