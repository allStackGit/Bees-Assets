using Assets.Scripts.Entities.Ships;

namespace Assets.Scripts.Entities.Projectiles
{
    /// <summary>
    /// Side-effect-free projectile contact rules. Damage accounting and lifecycle remain
    /// in Projectile/Ship; this class defines only whether a contact is eligible.
    /// </summary>
    public static class ProjectileDamagePolicy
    {
        public static bool CanBasicProjectileDamage(
            int shooterSide,
            ConfigData.ShipTypes shooterType,
            int targetSide,
            bool targetIsIgnored)
        {
            return !targetIsIgnored &&
                (shooterSide != targetSide || shooterType == ConfigData.ShipTypes.FireBarge);
        }

        public static bool CanExplosionDamage(
            int shooterSide,
            ConfigData.ShipTypes shooterType,
            int targetSide,
            ConfigData.ProjectileTypes projectileType,
            bool targetIsDead,
            bool isHarmless,
            bool alreadyHit)
        {
            if (targetIsDead || isHarmless || alreadyHit)
            {
                return false;
            }

            return projectileType == ConfigData.ProjectileTypes.FireTankExplosion ||
                shooterSide != targetSide ||
                shooterType == ConfigData.ShipTypes.FireBarge;
        }
    }
}
