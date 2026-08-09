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
