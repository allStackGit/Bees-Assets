using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Manages and displays a vertical charging bar.
/// </summary>
public class ChargingBar : MonoBehaviour
{
    public Ship Ship;
    public int TimeToCharge;
    public int PercentCharged;
    public int ChargingIncrement;
    public bool IsCharging;
    public bool IsFullyCharged;
    public GameObject BarFiller;

    private ScaledTimer _chargeBarTimer = new ScaledTimer();

    public void Create(Ship ship, int timeToCharge)
    {
        Ship = ship;
        TimeToCharge = timeToCharge;
        ChargingIncrement = 100 / (timeToCharge * 2);
    }

    public void Setup()
    {
        // ChargingBar is a child of a pooled Barge. A recharge timer from the previous
        // use must not survive into the newly configured ship.
        if (Ship?.Level != null)
        {
            Ship.Level.CancelTimer(_chargeBarTimer);
        }
        PercentCharged = 100;
        IsCharging = false;
        IsFullyCharged = true;
        SetBarFill();
    }

    public void ChargeBar()
    {
        PercentCharged = math.min(100, PercentCharged + ChargingIncrement);
        if (PercentCharged >= 100)
        {
            Ship.Level.CancelTimer(_chargeBarTimer);
            IsCharging = false;
            IsFullyCharged = true;
        }
        SetBarFill();
    }

    public void SetBarFill()
    {
        BarFiller.transform.localScale = new Vector2(PercentCharged / 100.0f, BarFiller.transform.localScale.y);
    }

    public void DrainBar(int percent = 100)
    {
        PercentCharged = math.max(0, PercentCharged - percent);
        IsFullyCharged = PercentCharged >= 100;
        SetBarFill();
        if (!IsCharging && !IsFullyCharged)
        {
            _chargeBarTimer.Reuse(.5f, ChargeBar, true);
            Ship.Level.AddTimer(_chargeBarTimer);
            IsCharging = true;
        }
    }
}
