using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships.Weapons;
using Assets.Scripts.Levels;
using Assets.Scripts.Settings;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public partial class Ship
    {
        public override void Create(Stage stage)
        {
            base.Create(stage);
            ShipStatBlock shipStats = ConfigData.GetShipInfo(ShipType);
            Sight = shipStats.Sight;
            Speed = shipStats.Speed;
            OriginalHealth = shipStats.Health;
            MaxHealth = OriginalHealth;
            Clearance = Stage.ShipClearances.GetValueOrDefault(ShipType);
            _healthBarFiller = HealthBar.transform.GetChild(0);
            _healthBarFillerSprite = _healthBarFiller.GetComponent<SpriteRenderer>();
            IsUserControlled = Side == ConfigData.Configuration.UserSide && Stage.DoesUserHaveController;
            RotationSpeed = Speed * ConfigData.Configuration.RotationMultiplier;
            IsMobile = Speed > 0;
            IsHiveMindControlled = !IsUserControlled;

            if (!Stage.IsTraining)
            {
                if (Side == ConfigData.Configuration.HumanSide)
                {
                    HasLeftRocketFlares = LeftRocketFlares.Count > 0;
                    HasCenterRocketFlares = CenterRocketFlares.Count > 0;
                    HasRightRocketFlares = RightRocketFlares.Count > 0;
                    HasRocketFlares = HasLeftRocketFlares || HasCenterRocketFlares || HasRightRocketFlares;
                }
                else HasRocketFlares = false;

                if (ShipAnimation != null)
                {
                    HasShipAnimation = true;
                    ShipAnimationController?.Setup();
                }
                if (HasRemainsShip)
                {
                    ShipRemains = Instantiate(Stage.Prefabs.ConvertShipTypeToRemainsPrefab[ShipType], Vector2.zero, Quaternion.identity)
                        .AddComponent<ShipRemains>();
                    ShipRemains.Create(this);
                }
                if (ShipType != ConfigData.ShipTypes.FireBarge)
                {
                    ShipExplosion = Instantiate(Stage.Prefabs.ConvertShipTypeToExplosionPrefab[ShipType], Vector2.zero, Quaternion.identity);
                    ShipExplosion.SetActive(false);
                    if (Stage.ActivateAudio)
                    {
                        ShipExplosionSoundEffect = ShipExplosion.GetComponent<AudioSource>();
                        HasShipExplosionSoundEffect = ShipExplosionSoundEffect != null;
                    }
                }
                _originalMiniMapIconScale = MiniMapIcon.transform.localScale;
            }
            else
            {
                Destroy(SortingGroup);
                Destroy(MiniMapIcon);
                LeftRocketFlares.ForEach(flare => Destroy(flare));
                CenterRocketFlares.ForEach(flare => Destroy(flare));
                RightRocketFlares.ForEach(flare => Destroy(flare));
                LeftRocketFlares.Clear();
                CenterRocketFlares.Clear();
                RightRocketFlares.Clear();
                HasRocketFlares = false;
                HasRemainsShip = false;
            }

            if (!Stage.IsRendering) Destroy(HealthBar);
            ConfigureSpecialRole(shipStats);
            CreateWeapons(shipStats);

            Turrets = Weapons.OfType<Turret>().ToList();
            HasWeapons = Weapons.Count > 0;
            HasTurrets = Turrets.Count > 0;
            MaxRange = HasWeapons ? Weapons.Max(weapon => weapon.Range) : 0;
            HalfMaxRange = MaxRange / 2;
            Firepower = HasWeapons ? Weapons.Sum(weapon => weapon.Firepower) : SpecialFirePower;
            DamagePerSecond = Turrets.Sum(turret => turret.DamagePerSecond);
            _maxRateOfFire = HasWeapons ? Weapons.Max(weapon => weapon.RateOfFire) : 2;
            _repeatRate = Mathf.Clamp(5f, _maxRateOfFire + 1, _maxRateOfFire + 2);
            _size = ConfigData.ShipSizes[ShipType] / ConfigData.PixelsPerUnit;
            OriginalTsv = Utilities.GetMaxTsv(ShipType);
            SetCurrentSpeed(Speed);

            if (IsUserControlled)
            {
                if (IsMobile)
                {
                    MovementMarker = Instantiate(Stage.Prefabs.MovementMarkerPrefab, Vector2.zero, Quaternion.identity);
                    MovementMarker.SetActive(false);
                    HasMovementMarker = true;
                }
                HasUserFogOfWarVision = true;
                FogOfWarVision.Create(this);
                Destroy(HiveMindVision.gameObject);
                OriginalColoredPrefabs.Insert(0, gameObject);
            }
            else
            {
                HiveMindVision.Create(this);
                Destroy(FogOfWarVision.gameObject);
            }
            if (HasProximityCollider) ProximityCollider.Create(this);
            LongestSide = Mathf.Max(GetWidth(), GetHeight());
            Deactivate();
        }

        private void ConfigureSpecialRole(ShipStatBlock shipStats)
        {
            if (ShipType == ConfigData.ShipTypes.Striker || ShipType == ConfigData.ShipTypes.Barge)
                SpecialFirePower = shipStats.Powers[0] / 3;
            else if (ShipType == ConfigData.ShipTypes.FireBarge)
                SpecialFirePower = shipStats.Powers[0] * shipStats.ProjectileValues[0];
            else if (ShipType == ConfigData.ShipTypes.YellowJacket)
                SpecialFirePower = shipStats.Powers[0] / 5;
            else if (ShipType == ConfigData.ShipTypes.CarpenterBee || ShipType == ConfigData.ShipTypes.Factory)
                IsMiningShip = true;
            else if (ShipType == ConfigData.ShipTypes.WarpGate)
                IsWarpGate = true;
            else if (ShipType == ConfigData.ShipTypes.Beehive)
                IsBeehive = true;
        }

        private void CreateWeapons(ShipStatBlock shipStats)
        {
            for (int i = 0; i < shipStats.ProjectileValues.Count; i++)
            {
                Weapon weapon = shipStats.WeaponTypes[i] switch
                {
                    ConfigData.WeaponTypes.Turret => gameObject.AddComponent<Turret>(),
                    ConfigData.WeaponTypes.LightCannon => gameObject.AddComponent<Turret>(),
                    ConfigData.WeaponTypes.RocketTurret => gameObject.AddComponent<Turret>(),
                    ConfigData.WeaponTypes.Eye => gameObject.AddComponent<Eye>(),
                    ConfigData.WeaponTypes.QueenEye => gameObject.AddComponent<QueenEye>(),
                    ConfigData.WeaponTypes.Bomb => gameObject.AddComponent<Bomb>(),
                    ConfigData.WeaponTypes.SplitShot => gameObject.AddComponent<LaserBuilder>(),
                    ConfigData.WeaponTypes.DualCannon => gameObject.AddComponent<DualCannon>(),
                    ConfigData.WeaponTypes.BeamCannon => gameObject.AddComponent<BeamCannon>(),
                    ConfigData.WeaponTypes.FullShipTurret => gameObject.AddComponent<FullShipTurret>(),
                    _ => null
                };
                if (weapon == null)
                {
                    Debug.LogError($"{Name}'s weapon #{i} doesn't have a proper weapon type: {shipStats.WeaponTypes[i]}");
                    continue;
                }
                if (weapon is Turret turret)
                {
                    turret.Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i],
                        shipStats.RatesOfFire[i], shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i], FireAtFrontOfShip,
                        shipStats.RotationRates[i]);
                }
                else
                {
                    weapon.Create(this, shipStats.WeaponTypes[i], shipStats.WeaponSoundTypes[i], shipStats.Ranges[i], shipStats.Powers[i], SpecialFirePower,
                        shipStats.RatesOfFire[i], shipStats.ProjectileValues[i], WeaponPrefabs[i], shipStats.ProjectileTypes[i]);
                }
                Weapons.Add(weapon);
            }
        }

        public virtual void Setup(Level level, FleetShip fleetShip, Squad squad, Vector2 offsetFromCenter)
        {
            Squad = squad;
            Level = level;
            Id = Level.State.GetId();
            FleetShip = fleetShip;
            OffsetFromCenter = offsetFromCenter;
            Health = OriginalHealth;
            Name = $"{FleetShip.Type} #{FleetShip.Id}";
            gameObject.name = Name;
            ClearData();
            if (IsHiveMindControlled) Level.State.HivemindShips[Side - 1].Add(Id, new HashSet<Ship>());
            IsSpawnedShip = FleetShip.Id < 0;

            if (!Level.Stage.IsTraining)
            {
                if (squad.HasCustomColor) Utilities.SetUIColor(MiniMapIcon, squad.Color);
                else if (Side == ConfigData.Configuration.HumanSide) Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("human"));
                else if (Side == ConfigData.Configuration.BeeSide) Utilities.SetUIColor(MiniMapIcon, ConfigData.GetUIColor("bee"));
                if (squad.HasCustomColor && HasShipAnimation) ShipAnimationController.RecolorAnimationSprites();
                MiniMapIcon.transform.localScale = _originalMiniMapIconScale * Level.Map.SizeMultiplier *
                    (ShipType == ConfigData.ShipTypes.Queen ? .75f : 1.5f);
            }

            Level.State.AddShip(this);
            if (IsWarpGate) Level.State.HasWarpGates = true;
            else if (IsBeehive) Level.State.HasBeehives = true;

            if (IsUserControlled && IsMobile)
            {
                MovementMarker.transform.SetParent(Level.Map.Transform);
                MovementMarker.name = $"{Name}'s Movement Marker";
                MovementMarker.GetComponent<SpriteRenderer>().color = Squad.HasCustomColor
                    ? Squad.Color
                    : ConfigData.GetUIColor(Side == ConfigData.Configuration.HumanSide ? "human" : "bee");
            }
            Weapons.ForEach(weapon => weapon.Setup());
            if (HasRemainsShip) ShipRemains.Setup();
            if ((ConfigData.Configuration.UserSide == Side || !Level.HasPlayer) &&
                (ShipType == ConfigData.ShipTypes.Factory || ShipType == ConfigData.ShipTypes.CarpenterBee))
                Level.State.MiningShips.Add(this);
            UpdateHealthBar();
            Activate();
        }

        public virtual void ClearData()
        {
            Rotation = OriginalRotation;
            Tsv = OriginalTsv;
            Transform.eulerAngles = new Vector3(0, 0, OriginalRotation);
            PathfindingDestination = Vector2.zero;
            PathfindingValue = null;
            PathfindingThreadComplete = false;
            IsPathfinding = false;
            PathfindingLifecycleId = unchecked(PathfindingLifecycleId + 1);
            PathfindingRequestId = 0;
            PathfindingCompletedRequestId = 0;
            _hasPendingPathfindingDestination = false;
            SetTargetCoordinates(Vector2.zero);
            HasTargetCoordinates = false;
            HasTargetDirection = false;
            FinalDestination = Vector2.zero;
            LastKilled = 0;
            CannotChangeMovementOrders = false;
            IsFollowingPath = false;
            InCombat = false;
            IsDead = false;
            AreRocketFlaresOutOfSync = false;
            DestinationQueue.Clear();
            NearbyAsteroids.Clear();
            TargetEnemyShipToFollow = null;
            Killer = null;
            KillerFleetShip = null;
            KillerSavedSquad = null;
            ProjectilesInFlight.Clear();
            WeaponsThatHaveUsWithinRange.Clear();
            SetToDefaultAngle();
            CanOverrideBounds = false;
            ShipsHit.Clear();
            _isInBounds = false;
        }

        protected void FixedUpdate()
        {
            if (Level.HasObstacles && PathfindingThreadComplete)
            {
                PathfindingThreadComplete = false;
                if (PathfindingCompletedRequestId == PathfindingRequestId)
                {
                    IsPathfinding = false;
                    MergePathfindingPaths();
                }
                else
                {
                    PathfindingValue = null;
                    HandleSupersededPathfindingRequest();
                }
            }
            Move();
            if (Stage.DebugLogger.IsDebugging || ShowDebug) UpdateDebugProperties();
        }

        public override void Deactivate()
        {
            Body.linearVelocity = Vector2.zero;
            base.Deactivate();
            StopAllCoroutines();
            if (HasWeapons)
            {
                Weapons.ForEach(weapon =>
                {
                    weapon.Deactivate();
                    if (IsUserControlled && weapon.HasRangeCircle) weapon.RangeCircle.SetActive(false);
                });
            }
            if (!IsUserControlled) HiveMindVision.Deactivate();
            if (HasProximityCollider) ProximityCollider.Deactivate();
            if (!Stage.IsTraining)
            {
                SortingGroup.enabled = false;
                MiniMapIcon.SetActive(false);
                if (HasRocketFlares)
                {
                    CenterRocketFlares.ForEach(flare => flare.SetActive(false));
                    RightRocketFlares.ForEach(flare => flare.SetActive(false));
                    LeftRocketFlares.ForEach(flare => flare.SetActive(false));
                }
                if (HasMovementMarker) MovementMarker.SetActive(false);
                HealthBar.SetActive(false);
            }
            else if (Stage.IsRendering) HealthBar.SetActive(false);
        }

        public override void Activate()
        {
            base.Activate();
            if (IsUserControlled)
            {
                if (Level.ActivateFogOfWar) FogOfWarVision.Activate();
            }
            else HiveMindVision.Activate();
            if (HasProximityCollider) ProximityCollider.Activate();
            if (HasWeapons) Weapons.ForEach(weapon => weapon.Activate());
            if (Stage.IsRendering) HealthBar.SetActive(true);
            if (!Stage.IsTraining)
            {
                SortingGroup.enabled = true;
                MiniMapIcon.SetActive(true);
            }
        }
    }
}
