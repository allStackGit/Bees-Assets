using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        private Vector2Int _convertedStart, _convertedDestination;
        private Vector2 _startPosition;
        private Collider2D _obstacleCollider;
        private const int RotationPrecisionDegrees = 3;
        private bool _hasPendingPathfindingDestination;
        private readonly ScaledTimer _asteroidDoubleCheckTimer = new ScaledTimer();
        private bool _isDoubleCheckingForAsteroids;
        public int _retries;
        private bool _tryingToFindPathAgain;
        private int _remainingEgressWaypoints;
        private readonly ScaledTimer _tryToFindPathAgainTimer = new ScaledTimer();
        private float _maxSpeed, _movementRotation;
        protected float _differenceInAngleToPoint;
        private readonly Vector3 _reverse = Vector3.forward * 180;

        public void MoveToPoint(Vector2 destination, bool foundObstacle = false)
        {
            if (CannotChangeMovementOrders)
            {
                WaitingTargetCoordinates = destination;
                HasWaitingTargetCoordinates = true;
                return;
            }

            destination = CanOverrideBounds ? destination : Level.ForceBounds(destination);
            if (IsPathfinding && !foundObstacle && destination == PathfindingDestination)
            {
                // Recurring command timers often restate the current destination. Reissuing an
                // identical request must not invalidate a worker whose result is still usable.
                // A newly detected collision obstacle is different: it changes the route and
                // must be allowed to invalidate the old snapshot once.
                return;
            }

            PathfindingDestination = destination;
            if (IsPathfinding)
            {
                _hasPendingPathfindingDestination = true;
                Level.Pathfinder.InvalidatePathRequest(this);
                return;
            }
            _hasPendingPathfindingDestination = false;

            if (Level.HasObstacles && IsInBounds())
            {
                _startPosition = Level.ForceBounds(GetPosition());
                if (!foundObstacle && Vector2.Distance(destination, TargetCoordinates) < ConfigData.CloseEnoughCoordinateVariance)
                {
                    return;
                }

                _obstacleCollider = foundObstacle ? Collider : GetObstacleInPath(destination);
                if (foundObstacle || _obstacleCollider != null)
                {
                    _tempObstacle = foundObstacle
                        ? null
                        : _obstacleCollider.GetComponent<Obstacle>() ?? _obstacleCollider.GetComponentInParent<Obstacle>();
                    if (foundObstacle || _tempObstacle != null)
                    {
                        _convertedStart = Level.Pathfinder.ConvertToMapCoordinates(_startPosition);
                        _convertedDestination = Level.Pathfinder.ConvertToMapCoordinates(destination);
                        ClearPreviousDesintation();
                        Level.Pathfinder.FindPath(this, _convertedStart.x, _convertedStart.y, _convertedDestination.x, _convertedDestination.y, GetClearance());
                        PathfindingDestination = destination;
                        return;
                    }
                }
            }

            ClearPreviousDesintation();
            IsFollowingPath = false;
            SetTargetCoordinates(destination);
            FinalDestination = TargetCoordinates;
            HasTargetCoordinates = true;
            MoveMovementMarker();
        }

        public void MoveToDirectionOfPoint(Vector2 directionPoint)
        {
            MoveInDirection(GetDegreesTowardsPoint(Level.ForceBounds(directionPoint)));
        }

        public void MoveInDirection(float direction)
        {
            if (CannotChangeMovementOrders)
            {
                return;
            }
            StopMoving("Got a new destination");
            IsFollowingPath = false;
            HasTargetCoordinates = false;
            HasTargetDirection = true;
            TargetDirection = direction;
        }

        public void FoundNearbyAsteroid(CollisionAsteroid asteroid)
        {
            if (!NearbyAsteroids.Contains(asteroid))
            {
                NearbyAsteroids.Add(asteroid);
            }
            if (!IsMobile || ShipType == ConfigData.ShipTypes.Queen)
            {
                return;
            }

            MoveToPoint(!IsFollowingPath && !HasTargetCoordinates ? GetPosition() : FinalDestination, true);
            if (!_isDoubleCheckingForAsteroids)
            {
                _isDoubleCheckingForAsteroids = true;
                _asteroidDoubleCheckTimer.Reuse(1, NearbyAsteroidDoubleCheck, true);
                Level.AddTimer(_asteroidDoubleCheckTimer);
            }
        }

        public void NearbyAsteroidDoubleCheck()
        {
            NearbyAsteroids.RemoveAll(asteroid => asteroid == null || asteroid.IsDead);
            if (NearbyAsteroids.Count > 0)
            {
                // This timer runs every second while dynamic asteroids remain nearby. Invalidating
                // an active A* request here only discards its result; the worker keeps running.
                // Let that search settle before refreshing the same final destination.
                if (!IsPathfinding)
                {
                    MoveToPoint(FinalDestination);
                }
            }
            else
            {
                Level.CancelTimer(_asteroidDoubleCheckTimer);
                _isDoubleCheckingForAsteroids = false;
            }
        }

        public void LeftNearbyAsteroid(CollisionAsteroid asteroid)
        {
            NearbyAsteroids.Remove(asteroid);
        }

        public void HandleSupersededPathfindingRequest()
        {
            if (!_hasPendingPathfindingDestination || IsDead)
            {
                return;
            }
            Vector2 latestDestination = PathfindingDestination;
            _hasPendingPathfindingDestination = false;
            IsPathfinding = false;
            MoveToPoint(latestDestination);
        }

        private void MergePathfindingPaths()
        {
            if (PathfindingValue != null && PathfindingValue.Points.Count > 0)
            {
                _retries = 0;
                List<Vector2> pathPoints = PathfindingValue.Points;
                DestinationQueue.Clear();
                for (int i = 0; i < pathPoints.Count; i++)
                {
                    DestinationQueue.Enqueue(pathPoints[i]);
                }
                _remainingEgressWaypoints = PathfindingValue.EgressPointCount;
                SkipClosePathWaypoints();
                FinalDestination = pathPoints[pathPoints.Count - 1];
                SetTargetCoordinates(DestinationQueue.Dequeue());
                if (_remainingEgressWaypoints > 0)
                {
                    _remainingEgressWaypoints--;
                }
                IsFollowingPath = true;
                HasTargetCoordinates = true;
                PathfindingValue = null;
                DebugWalkablePointNodes.Clear();
                MoveMovementMarker();
            }
            else if (_retries < 5 && !_tryingToFindPathAgain)
            {
                StopMoving("Could not find a path to destination");
                _tryingToFindPathAgain = true;
                _tryToFindPathAgainTimer.Reuse(2, TryToFindPathAgain);
                Level.AddTimer(_tryToFindPathAgainTimer);
                _retries++;
            }
            else if (!_tryingToFindPathAgain)
            {
                _retries = 0;
                StopMoving("Could not find a safe path to destination");
            }
            IsPathfinding = false;
        }

        public void TryToFindPathAgain()
        {
            _tryingToFindPathAgain = false;
            MoveToPoint(PathfindingDestination);
        }

        private void CheckForDirectPath()
        {
            if (!HasObstacleInPath(FinalDestination))
            {
                IsFollowingPath = false;
                DestinationQueue.Clear();
            }
        }

        private void MoveMovementMarker()
        {
            if (!HasMovementMarker || !Squad.IsSelected)
            {
                return;
            }
            if (HasTargetCoordinates)
            {
                MovementMarker.transform.localPosition = FinalDestination;
                MovementMarker.SetActive(true);
            }
            else
            {
                MovementMarker.SetActive(false);
            }
        }

        public void SetTargetCoordinates(Vector2 value)
        {
            TargetCoordinates = value;
        }

        private void Move()
        {
            if (HasBrain && !Squad.IsUserControlled)
            {
                NNDirectionalMovement();
            }
            else if (HasTargetCoordinates)
            {
                MoveToTargetCoordinates();
                Squad.MoveSquadBox();
            }
            else if (HasTargetDirection)
            {
                MoveInDirection();
                Squad.MoveSquadBox();
            }
        }

        private void NNDirectionalMovement()
        {
            // The historical Brain owns ShouldDetonate. Dedicated ML-Agents training disables
            // that Brain and controls suicide/special actions through its explicit special branch.
            if (ShouldDetonate && Stage.ActivateBrains)
            {
                if (ShipType == ConfigData.ShipTypes.Striker) ((Striker)this).TryToDropBombs();
                else if (ShipType == ConfigData.ShipTypes.YellowJacket) ((YellowJacket)this).TryToDetonate();
                else if (ShipType == ConfigData.ShipTypes.FireBarge) ((FireBarge)this).Detonate();
            }

            if (Direction == 360)
            {
                Body.linearVelocity = Vector2.zero;
                IsMoving = false;
                return;
            }
            if (!HasTargetCoordinates || DistanceToPoint(TargetCoordinates) > GetHeight())
            {
                Utilities.TimedRotationDifference(this, Direction, RotationSpeed);
            }
            _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
            // Directional controllers must honor gameplay speed state just like the normal movement
            // path. This is required for Barge charge speed and any other temporary speed changes.
            _tempVelocity = new Vector2(CurrentSpeed * Mathf.Sin(_tempAngle), -CurrentSpeed * Mathf.Cos(_tempAngle));
            Body.linearVelocity = _tempVelocity;
            IsMoving = true;
        }

        public void SetMovementVelocity()
        {
            if (HasTargetCoordinates) _movementRotation = GetDegreesTowardsPoint(TargetCoordinates);
            else if (HasTargetDirection) _movementRotation = TargetDirection;
            else
            {
                Debug.LogWarning($"{Name} is trying to move without target coordinates or target direction");
                return;
            }

            IsMoving = true;
            _differenceInAngleToPoint = TryRotateTowardsMovementTarget(_movementRotation);
            bool updatedVelocity = false;
            if (_differenceInAngleToPoint != 0 || Stage.FixedUpdates % 10 == 0 || _maxSpeed != CurrentSpeed)
            {
                _maxSpeed = CurrentSpeed;
                _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
                _tempVelocity = new Vector2(_maxSpeed * Mathf.Sin(_tempAngle), -_maxSpeed * Mathf.Cos(_tempAngle));
                updatedVelocity = true;
            }
            if (!updatedVelocity && Body.linearVelocity.sqrMagnitude > 0) _tempVelocity = Body.linearVelocity;
            else if (!updatedVelocity)
            {
                _maxSpeed = CurrentSpeed;
                _tempAngle = (Rotation - 180) * Mathf.Deg2Rad;
                _tempVelocity = new Vector2(_maxSpeed * Mathf.Sin(_tempAngle), -_maxSpeed * Mathf.Cos(_tempAngle));
            }
            Body.linearVelocity = _tempVelocity;
            if (HasRocketFlares) SetRocketFlares();
        }

        private void MoveInDirection()
        {
            SetMovementVelocity();
        }

        private void MoveToTargetCoordinates()
        {
            _tempDistance = DistanceToPoint(TargetCoordinates);
            SetMovementVelocity();
            if (IsCloseEnoughToTargetCoordinates(_tempDistance))
            {
                EndDestination();
            }
            else if (HasTargetEnemyShipToFollow &&
                     !(Squad.HasCommand && Squad.HasMovementAttackType) &&
                     IsShipWithinRange(TargetEnemyShipToFollow))
            {
                SetCurrentSpeed(TargetEnemyShipToFollow.CurrentSpeed);
                if (Level.Stage.FixedUpdates % 10 == 0 && DistanceTo(TargetEnemyShipToFollow) < HalfMaxRange)
                {
                    EndDestination();
                }
                return;
            }

            if (Squad.IsMatchingSpeed && CurrentSpeed != Squad.CurrentSpeed && Squad.CurrentSpeed > 0)
                SetCurrentSpeed(Squad.CurrentSpeed);
            else if (!Squad.IsMatchingSpeed && CurrentSpeed != Speed)
                SetCurrentSpeed(Speed);
        }

        private void EndDestination(string reason = null)
        {
            SkipClosePathWaypoints();
            if (DestinationQueue.Count > 0)
            {
                SetTargetCoordinates(DestinationQueue.Dequeue());
                if (_remainingEgressWaypoints > 0) _remainingEgressWaypoints--;
            }
            else
            {
                StopMoving(reason);
            }
        }

        public virtual bool IsCloseEnoughToTargetCoordinates(float distance)
        {
            return distance < ConfigData.ShipTurningRadius;
        }

        private void SkipClosePathWaypoints()
        {
            if (!IsFollowingPath && PathfindingValue == null) return;
            float waypointRadius = GetPathWaypointRadius();
            while (_remainingEgressWaypoints == 0 && DestinationQueue.Count > 1 && DistanceToPoint(DestinationQueue.Peek()) < waypointRadius)
            {
                DestinationQueue.Dequeue();
            }
        }

        private float GetPathWaypointRadius()
        {
            return Mathf.Max(ConfigData.ShipTurningRadius, Mathf.Min(GetHalfWidth(), GetHalfHeight()) * 0.75f);
        }

        private float TryRotateTowardsMovementTarget(float targetRotation)
        {
            float rotationDifference = Mathf.DeltaAngle(Rotation, targetRotation);
            if (rotationDifference > RotationPrecisionDegrees)
            {
                ApplyMovementRotation(Stage.FixedDeltaTime * RotationSpeed);
                return rotationDifference;
            }
            if (rotationDifference < -RotationPrecisionDegrees)
            {
                ApplyMovementRotation(-Stage.FixedDeltaTime * RotationSpeed);
                return rotationDifference;
            }
            return 0;
        }

        private void ApplyMovementRotation(float rotationStep)
        {
            Transform.Rotate(Vector3.forward * rotationStep);
            Rotation += rotationStep;
            for (int i = 0; i < Turrets.Count; i++)
            {
                Turrets[i].Rotation += rotationStep;
            }
        }

        public void StopMoving(string reason = null)
        {
            if (!IsMobile) return;
            Level.CancelTimer(_tryToFindPathAgainTimer);
            _tryingToFindPathAgain = false;
            if (IsPathfinding)
            {
                _hasPendingPathfindingDestination = false;
                Level.Pathfinder.InvalidatePathRequest(this);
                IsPathfinding = false;
            }
            SetTargetCoordinates(Vector2.zero);
            FinalDestination = Vector2.zero;
            Body.linearVelocity = Vector2.zero;
            IsMoving = false;
            // HasBrain directional movement does not consume HasTargetCoordinates/HasTargetDirection.
            // Reset its sentinel too so a gameplay StopMoving call actually stops an RL-controlled ship.
            if (HasBrain && !Squad.IsUserControlled)
            {
                Direction = 360;
            }
            ClearPreviousDesintation();
            if (HasRocketFlares)
            {
                CenterRocketFlares.ForEach(flare => flare.SetActive(false));
                LeftRocketFlares.ForEach(flare => flare.SetActive(false));
                RightRocketFlares.ForEach(flare => flare.SetActive(false));
            }
            MoveMovementMarker();
        }

        public void ClearPreviousDesintation()
        {
            HasTargetCoordinates = false;
            HasTargetDirection = false;
            TargetDirection = 0;
            _remainingEgressWaypoints = 0;
            if (IsFollowingPath)
            {
                IsFollowingPath = false;
                DestinationQueue.Clear();
            }
        }

        public void SetToDefaultAngle()
        {
            if (Side == ConfigData.Configuration.AISide)
            {
                Transform.eulerAngles = _reverse;
                Rotation = 180;
            }
        }

        public void SetCurrentSpeed(float speed, float maxSpeed = -1)
        {
            if (maxSpeed == -1) maxSpeed = Speed;
            CurrentSpeed = Mathf.Min(speed, maxSpeed);
        }
    }
}
