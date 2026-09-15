using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Entities.Ships
{
    public class ProximityCollider : MonoBehaviour
    {
        public CircleCollider2D Collider;
        public HashSet<Ship> NearbyEnemyShips = new HashSet<Ship>(ReferenceIdentityComparer<Ship>.Instance);
        public Ship Ship;

        private readonly Dictionary<Ship, int> _enemyOverlapCounts =
            new Dictionary<Ship, int>(ReferenceIdentityComparer<Ship>.Instance);
        
        public void Create(Ship ship)
        {
            Ship = ship;
            int proximityRange = Ship.Sight;
            if (proximityRange == 0)
            {
                proximityRange = Ship.MaxRange;
            }
            Collider.radius = proximityRange;
            Collider.isTrigger = true;
        }

        public void Activate()
        {
            NearbyEnemyShips.Clear();
            _enemyOverlapCounts.Clear();

            Collider.enabled = true;
            enabled = true;
        }
        public void Deactivate()
        {
            NearbyEnemyShips.Clear();
            _enemyOverlapCounts.Clear();
            Collider.enabled = false;
            enabled = false;
        }

        protected void OnTriggerEnter2D(Collider2D collider)
        {
            Ship nearbyShip = ResolveShip(collider);
            if (!IsEnemyShip(nearbyShip))
            {
                return;
            }

            if (_enemyOverlapCounts.TryGetValue(nearbyShip, out int overlapCount))
            {
                _enemyOverlapCounts[nearbyShip] = overlapCount + 1;
            }
            else
            {
                _enemyOverlapCounts.Add(nearbyShip, 1);
                NearbyEnemyShips.Add(nearbyShip);
            }
            //Debug.Log($"Just added {nearbyShip} to {Ship} NearbyShips");

        }
        protected void OnTriggerExit2D(Collider2D collider)
        {
            Ship nearbyShip = ResolveShip(collider);
            if (nearbyShip == null || !_enemyOverlapCounts.TryGetValue(nearbyShip, out int overlapCount))
            {
                return;
            }

            if (overlapCount <= 1)
            {
                _enemyOverlapCounts.Remove(nearbyShip);
                NearbyEnemyShips.Remove(nearbyShip);
            }
            else
            {
                _enemyOverlapCounts[nearbyShip] = overlapCount - 1;
            }
            //Debug.Log($"Just removed {nearbyShip} from {Ship} NearbyShips");
        }

        private static Ship ResolveShip(Collider2D collider)
        {
            if (collider == null)
            {
                return null;
            }

            return collider.GetComponent<Ship>() ?? collider.GetComponentInParent<Ship>();
        }

        private bool IsEnemyShip(Ship nearbyShip)
        {
            return nearbyShip != null && Ship != null && nearbyShip != Ship && nearbyShip.Side != Ship.Side;
        }
    }
}