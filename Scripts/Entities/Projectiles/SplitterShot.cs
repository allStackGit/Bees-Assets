

using Assets.Scripts.Entities.Ships;
using Assets.Scripts.Levels;
using System.Collections;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

namespace Assets.Scripts.Entities.Projectiles
{
    public class SplitterShot : LaserShot
    {
        public int SplitCount;
        public GameObject SplitLaserPrefab;
        public override void ContactTarget(Ship target)
        {
            //Debug.Log($"Split shot hit {target.name}");
            KillSequence(target);
        }

        public void KillSequence(Ship target)
        {
            if (HasExplosion)
            {
                Explosion = Instantiate(ExplosionAnimationPrefab, GetPosition() + Level.GetPosition(), Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
            }
            Split(target);
            Kill();
        }

        public override void KillSequence()
        {
            if (HasExplosion)
            {
                Explosion = Instantiate(ExplosionAnimationPrefab, GetPosition() + Level.GetPosition(), Quaternion.identity);
                Explosion.transform.parent = Level.Map.transform;
            }
            Split(null);
            Kill();
        }



        public void Split(Ship target) // [projectile-method] [note]
        {
            //Debug.Log($"Splitting into {SplitCount} more shots");

            for (int shotNumber = 0; shotNumber < SplitCount; shotNumber++)
            {
                float localAngle = Angle * Mathf.Rad2Deg; 
                if (shotNumber == 0)
                {
                    localAngle += 45;
                }else if (shotNumber == 1)
                {
                    localAngle -= 45;
                }
                else if (shotNumber == 2)
                {
                    localAngle += 90;
                }
                else if (shotNumber == 3)
                {
                    localAngle -= 90;
                }
                float radians = localAngle * Mathf.Deg2Rad;
                //Debug.Log($"Split shot #{shotNumber} is at localAngle: {localAngle}, coming from eulerAngle: {transform.localEulerAngles.z}, and now at world" +
                //    $"angle: {worldAngle} (rad) : {radians}");


                Vector3 startingPosition = GetPosition();
                GameObject shot =  Instantiate(SplitLaserPrefab, startingPosition, Quaternion.identity);
                shot.transform.parent = Level.Map.transform;
                LaserShot projectile = (LaserShot)shot.GetComponent(typeof(LaserShot));
                GameState state = Level.GetState();
                projectile.Setup(Level, Shooter.Side, state.GetId(), Weapon, Shooter, Target, startingPosition, radians, Weapon.Range, (int) (Weapon.Power / 1.5f));
                projectile.ShipsToIgnore.Add(target);
                Shooter.ProjectilesInFlight.Add(projectile);

            }

        }
    }
}