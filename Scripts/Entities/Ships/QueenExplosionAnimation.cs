using Assets.Scripts.Entities.Ships;
using UnityEngine;

public class QueenExplosionAnimation : ShipExplosionAnimation
{
    public GameObject Container;
    public ShipRemains Remains;
    public Queen Queen;
    public override void Kill()
    {
        Container.SetActive(false);
        Remains.Place();
        Queen.CompleteDeathAnimation();
    }
}
