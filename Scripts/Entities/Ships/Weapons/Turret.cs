using UnityEngine;

namespace Assets.Scripts.Entities.Ships.Weapons
{
    public partial class Turret : Weapon
    {
        public bool ShouldFireAtFrontOfShip, IsAimedAtTarget;
        public bool IsFiringManually, HasTargetingMarker;
        public bool ReadyToFire;
        public int TargetingPasses, PassesPerFire;
        public float TargetingRate;
        public float Rotation;
        public float OriginalRotation;
        public float DamagePerSecond;
        public Vector2 TargetPoint;
        public GameObject TargetingMarker;
        public CollisionAsteroid TargetAsteroid;
        public bool HasTargetAsteroid;
        public bool IsFiringAtAsteroid;

        public bool ShouldFireAtAsteroid =>
            !Ship.IsCeaseFire && HasTargetAsteroid && TargetShip == null && !TargetAsteroid.IsDead && TargetAsteroid.HasEnteredMap;

        private ScaledTimer _targetingSequenceTimer = new ScaledTimer();

        public virtual void Create(
            Ship ship,
            ConfigData.WeaponTypes type,
            ConfigData.WeaponSoundTypes weaponSound,
            int range,
            int power,
            float rateOfFire,
            float projectileValue,
            GameObject piece,
            ConfigData.ProjectileTypes projectileType,
            bool fireAtFrontOfShip,
            float rotationRate)
        {
            base.Create(ship, type, weaponSound, range, power, 0, rateOfFire, projectileValue, piece, projectileType);
            OriginalRotation = PieceTransform.eulerAngles.z;
            Rotation = OriginalRotation;
            ShouldFireAtFrontOfShip = fireAtFrontOfShip;
            PassesPerFire = 3;
            TargetingRate = RateOfFire / PassesPerFire;
            RotationRate = rotationRate;
            DamagePerSecond = RateOfFire > 0 ? Power / RateOfFire : 0;

            if (Ship.IsUserControlled)
            {
                TargetingMarker = Instantiate(Stage.Prefabs.TargetingMarkerPrefab, Vector2.zero, Quaternion.identity);
                TargetingMarker.SetActive(false);
                HasTargetingMarker = true;
            }
        }

        public override void Setup()
        {
            base.Setup();
            if (Ship.IsUserControlled)
            {
                TargetingMarker.transform.SetParent(Level.Map.Transform);
                TargetingMarker.name = $"{Name}'s Targeting Marker";
            }
            if (RateOfFire > 0)
            {
                _targetingSequenceTimer.Reuse(TargetingRate, TargetingSequence, true);
                Level.AddTimer(_targetingSequenceTimer);
            }
        }

        public override void Deactivate()
        {
            base.Deactivate();
            if (HasTargetingMarker)
            {
                TargetingMarker.SetActive(false);
            }
        }

        public override void CancelTimer()
        {
            Level.CancelTimer(_targetingSequenceTimer);
            base.CancelTimer();
        }

        public override void ClearData()
        {
            base.ClearData();
            ResetRotation();
            TargetingPasses = 0;
            IsAimedAtTarget = false;
            IsFiringManually = false;
            ReadyToFire = false;
            TargetPoint = Vector2.zero;
            TargetAsteroid = null;
            HasTargetAsteroid = false;
            IsFiringAtAsteroid = false;
        }

        public virtual void ResetRotation()
        {
            Rotation = OriginalRotation;
            PieceTransform.eulerAngles = new Vector3(0, 0, OriginalRotation);
        }

        private void FixedUpdate()
        {
            if (!Ship.IsCeaseFire)
            {
                Aim();
            }
        }

        public override Vector2 GetPosition()
        {
            return Ship.Level.Map.Transform.InverseTransformPoint(PieceTransform.position);
        }
    }
}
