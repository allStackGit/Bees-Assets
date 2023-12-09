

using Assets.Scripts.Level;
using Assets.Scripts.Scenes;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class Weapon : MonoBehaviour
    {

        public Ship Ship, TargetShip;
        public int Range, Power;
        public float RateOfFire, ProjectileValue, SpecialFirepower;
        public GameObject Piece, ProjectilePrefab, RangeCircle;
        public List<Ship> CachedTargetingQueue = new List<Ship>();
        public string CachedShootingStrategy;
        public bool IsUsingCachedTargetingQueue;
        public float Firepower => Utilities.CalculateFirepower(Power, Range, RateOfFire, ProjectileValue, SpecialFirepower);
        public bool CeaseFire => Ship.Squad.CeaseFire;
        public bool HasTargetShip => TargetShip != null;
        public LevelStage Level => Ship.Level;
        public Squad Squad => Ship.Squad;
        public int Side => Ship.Side;


        public string __NotShootingReason;
        public virtual void Setup(Ship ship, int range, int power, float specialFirePower, float rateOfFire, float projectileValue, GameObject piece, 
            GameObject projectilePrefab)
        {
            Ship = ship;
            Range = range;
            Power = power;
            SpecialFirepower = specialFirePower;
            //Power = 10;
            ProjectileValue = projectileValue;
            RateOfFire = rateOfFire;
            //Piece =  Instantiate(piece, Vector2.zero, Quaternion.identity);
            //Piece.transform.localScale = Ship.RelativeSizeScale();
            Piece = piece;
            ProjectilePrefab = projectilePrefab;

            Transform HasRangeCircle = Piece.transform.Find("Range Circle");
            if (HasRangeCircle != null)
            {
                RangeCircle = HasRangeCircle.gameObject;
                RangeCircle.transform.localScale = new Vector3(Range*2, Range*2, 0);
            }
            //Piece.transform.parent = ship.transform;
            //Piece.transform.localPosition = (Vector2)piece.transform.position;

        }


        // Shooting methods
        protected virtual void SetTargetShip(Ship targetShip)
        {
            //Debugger.Log("Setting target ship");
            TargetShip = targetShip;
        }
        public bool DetermineTargetShip(List<Ship> ships, bool useShipStatus)
        {
            //Debugger.Log($"Determining Target ship with {FleetShip.Name}!");
            bool foundTarget = false;

            for (int i = 0; i < ships.Count; i++)
            {
                Ship potentialTargetShip = ships[i];
                //Debugger.Log($"{name} is firing at {ship.name} which is priority #{i} in because the Shooting strategy is {Squad.GetShootingStrategy()}.");
                if (!foundTarget && potentialTargetShip != null)
                {
                    if (CheckIfShipIsValidTarget(potentialTargetShip)) // if the target ship is within range of this weapon
                    {
                        /*
                        Check to make sure that the damage already sent towards the ship is less than the health of the ship previously
                        calculated.
                         */
                        ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(potentialTargetShip);
                        if (useShipStatus)
                        {
                            if (shipDamageStatus.totalDamageSentToShip < shipDamageStatus.health)
                            {
                                SetTargetShip(potentialTargetShip);
                                foundTarget = true;
                            }
                        }
                        else
                        {
                            SetTargetShip(potentialTargetShip);
                            foundTarget = true;
                        }

                    }
                    else
                    {
                        //Debugger.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship {potentialTargetShip.Name} is out of range");
                        __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the potential target ship {potentialTargetShip.Name} is out of range";
                    }
                }
                else
                {
                    if (potentialTargetShip == null)
                    {
                        //Debugger.Log($"{Ship.Name} is not find a target for {Piece.name} because the potential target ship is null");
                        __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the potential target ship is null";
                        // Empty the cached queue because it has bad results
                        CachedTargetingQueue.Clear();
                    }
                }
            }

            if (ships.Count == 0)
            {
                //Debugger.Log($"{Ship.Name} is not find a target for {Piece.name} because the ship queue is empty");
                __NotShootingReason = $"{Ship.Name} is not find a target for {Piece.name} because the ship queue is empty";
                CachedTargetingQueue.Clear();
            }
            return foundTarget;
        }
        protected virtual bool CheckIfShipIsValidTarget(Ship potentialTargetShip)
        {
            return IsShipWithinRange(potentialTargetShip);
        }
        /// <summary>
        /// Called every 1/3 Rate of Fire. Sends the targeting queue to DetermineTargetShip
        /// Every time this method is called, a target ship should be selected if there is one available
        /// </summary>
        protected void Targeting()
        {
            //Debugger.Log($"Targeting! with {Ship.FleetShip.Name}");

            //if (Level.IsTestFiring) // fire a projectile at the mouse if we're testing that
            //{
            //    Level.AddProjectile(this.ProjectilePrefab, this, GetPosition(), AngleToPoint(Level.InputManager.GetMousePosition()));
            //}
            TargetShip = null;
            if (!Level.IsPaused && !CeaseFire)
            {
                if (Ship.IsUserControlled) // user controlled fire sequence
                {
                    List<Ship> queue = MakeTargetingQueue();
                    if (!DetermineTargetShip(queue, true))
                    {
                        DetermineTargetShip(queue, false);
                    }
                }
                else
                {
                    if ((Ship.HasCommand || Ship.HasBrain) && !Squad.IsRetreating) // if you've got a command, and you're not retreating
                    {
                        List<Ship> queue = MakeTargetingQueue();
                        if (!DetermineTargetShip(queue, true))
                        {
                            DetermineTargetShip(queue, false);
                        }
                    }
                    else
                    {
                        if (!Ship.HasCommand)
                        {
                            //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it doesn't have a command");
                            __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it doesn't have a command";
                        }
                        else if (Squad.IsRetreating)
                        {
                            //Debugger.Log($"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating");
                            __NotShootingReason = $"{Ship.Name} is not firing {Piece.name} because it is AI controlled and it's retreating";
                        }
                    }
                }
            }

        }
        protected virtual List<Ship> GetPotentialEnemyTargetShips()
        {
            List<Ship> queue;
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                List<Ship> enemyShips = GetEnemyShipsWithinRange();
                if (enemyShips.Count > 0)
                {
                    //Debugger.Log($"{Ship.Name} is targeting {enemyShips.Count} enemy ships");
                    queue = enemyShips;
                }
                else
                {
                    queue = GetAllEnemyShipsWithinRange();
                    //Debugger.Log($"{Ship.Name} is not targeting specific enemy ships");
                }
            }
            else
            {
                queue = GetAllEnemyShipsWithinRange();
            }
            if (CachedShootingStrategy == Ship.ShootingStrategy && queue.Count == CachedTargetingQueue.Count)
            {
                IsUsingCachedTargetingQueue = true;
                return CachedTargetingQueue;
            }
            IsUsingCachedTargetingQueue = false;
            return queue;
        }
        public List<Ship> MakeTargetingQueue()
        {
            //Debugger.Log($"Making targeting queue. The squad is using {Squad.GetShootingStrategy()}");
            List<Ship> queue = GetPotentialEnemyTargetShips();
            string strategy = Ship.ShootingStrategy;
            CachedShootingStrategy = strategy;
            CachedTargetingQueue = queue;
            if (strategy != null && !IsUsingCachedTargetingQueue)
            {
                switch (strategy)
                {
                    case "First Seen":
                        return queue;
                    case "Random":
                        System.Random rnd = Utilities.GetRandom();
                        return queue.OrderBy(s => rnd.Next()).ToList();
                    case "Revenge":
                        return queue.OrderByDescending(s => s.LastKilled).ToList();
                    case "Most Dangerous":
                        return queue.OrderByDescending(s => s.FleetShip.DamageDone).ToList();
                    case "Least Health":
                        return queue.OrderBy(s => s.Health).ToList();
                    case "Most Health":
                        return queue.OrderByDescending(s => s.Health).ToList();
                    case "Most Powerful":
                        return queue.OrderByDescending(s => s.Firepower).ToList();
                    case "Least Powerful":
                        return queue.OrderBy(s => s.Firepower).ToList();
                    case "Closest":
                        queue.Sort((a, b) => (int)(DistanceTo(a) - DistanceTo(b)));
                        return queue.ToList();
                    case "Furthest":
                        queue.Sort((a, b) => (int)(DistanceTo(b) - DistanceTo(a)));
                        return queue.ToList();
                    case "Most Range":
                        return queue.OrderByDescending(s => s.Range).ToList();
                    case "Least Range":
                        return queue.OrderBy(s => s.Range).ToList();
                    case "Fastest":
                        return queue.OrderByDescending(s => s.Speed).ToList();
                    case "Slowest":
                        return queue.OrderBy(s => s.Speed).ToList();
                    case "Most Valuable":
                        return queue.OrderByDescending(s => s.Tsv).ToList();
                    case "Least Valuable":
                        return queue.OrderBy(s => s.Tsv).ToList();
                    default:
                        if (strategy.StartsWith("Type "))
                        {
                            string type = strategy.Substring(5);
                            queue.Sort((a, b) =>
                            {
                                //Debugger.Log($"Strategy: {strategy}, Type: {type}, A ShipTypeLetter: {a.ShipTypeLetter}, B ShipTypeLetter: {b.ShipTypeLetter}");
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

        protected virtual void SendProjectile() // [projectile-method] [note]
        {
            //Debugger.Log("Sending basic projectile");
            ShipDamageStatus shipDamageStatus = Squad.GetShipDamageStatus(TargetShip);
            shipDamageStatus.totalDamageSentToShip += Power;
            //Vector2 targetPoint = TargetShip.GetPosition();
            //if (FireAtFrontOfShip)
            //{
            //    Vector2 frontOfShip = targetPoint + new Vector2(0, TargetShip.GetHalfHeight() - 1);
            //    targetPoint = Utilities.RotatePointAroundPoint(targetPoint, frontOfShip, TargetShip.GetRotation() * Mathf.Deg2Rad);

            //}
            //float angle = AngleToPoint(targetPoint);

            //Level.AddProjectile(ProjectilePrefab, this, GetPosition(), angle);
            //Ship.FleetShip.ShotsFired++;

        }
        public List<Ship> GetEnemyShipsWithinRange() 
        {
            if (Ship.Squad.HasEnemy && Ship.Squad.IsAttacking)
            {
                return Ship.Squad.Command.Enemy.GetShips().Where((s) => IsShipWithinRange(s)).ToList();
            }
            else
            {
                return new List<Ship>();
            }
        }
        public List<Ship> GetAllEnemyShipsWithinRange()
        {
            return Level.GetState().GetAllEnemyShips(Side).Where((s) => IsShipWithinRange(s)).ToList();
        }

        // distance and position methods
        public bool IsShipWithinRange(Ship ship)
        {
            return IsPointWithinRange(ship.GetPosition());
        }
        public virtual bool IsPointWithinRange(Vector2 point)
        {
            return DistanceToPoint(point) <= Range;
        }
        public float DistanceToPoint(Vector2 point)
        {
            return Vector2.Distance(GetPosition(), point);
        }
        public float DistanceTo(Entity entity)
        {
            return DistanceToPoint(entity.GetPosition());
        }
        public Vector2 GetPosition()
        {
            return Ship.Level.Map.transform.InverseTransformPoint(Piece.transform.position);
        }
        public Vector2 GetLocalPosition()
        {
            return Piece.transform.localPosition;
        }
        public float GetRotation()
        {
            return Piece.transform.eulerAngles.z;
        }
        public float GetLocalRotation()
        {
            return Piece.transform.localEulerAngles.z;
        }
        public float AngleToPoint(Vector3 point)
        {
            return Utilities.AngleBetweenPoints(GetPosition(), point);
        }
        public float GetDegreesTowardsPoint(Vector2 point)
        {
            float radians = AngleToPoint(point);
            float degrees = radians * Mathf.Rad2Deg;
            //Debugger.Log($"Angle towards movement point before adjustment {degrees}");
            if (degrees > 0) // if the angle is greater than PI, subtract 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees - 180);

            }
            if (degrees < 0) // if the angle is less than negative PI, add 2 PI to get the equivilent negative angle
            {
                degrees = Mathf.Abs(degrees) + 180;
            }
            //Debugger.Log($"Angle towards movement point after adjustment {degrees}");
            return degrees;
        }
        private void OnDestroy()
        {
            CancelInvoke();
        }

        // UI Methods
        public void ShowRange()
        {
            RangeCircle.SetActive(true);
        }

        public void HideRange()
        {
            RangeCircle.SetActive(false);
        }
    }
}