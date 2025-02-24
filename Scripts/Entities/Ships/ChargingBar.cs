using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Manages and displays a vertical charging bar
/// </summary>
public class ChargingBar : MonoBehaviour
{
    /// <summary>
    /// The Ship that this charging bar belongs to
    /// </summary>
    public Ship Ship;
    /// <summary>
    /// How long it takes for the bar to fully charge, in seconds
    /// </summary>
    public int TimeToCharge;
    /// <summary>
    /// How much of the bar is charged
    /// </summary>
    public int PercentCharged;
    /// <summary>
    /// How much of the bar gets charged each charging cycle
    /// </summary>
    public int ChargingIncrement;
    /// <summary>
    /// Whether or not the bar is currently charging
    /// </summary>
    public bool IsCharging;
    /// <summary>
    /// Whether or not the bar is fully charged
    /// </summary>
    public bool IsFullyCharged;
    /// <summary>
    /// The green filler that shows how much the bar is filled
    /// </summary>
    public GameObject BarFiller;
    public void Create(Ship ship, int timeToCharge)
    {
        Ship = ship;
        TimeToCharge = timeToCharge;
        ChargingIncrement = 100 / (timeToCharge * 2);
    }
    public void Setup()
    {
        PercentCharged = 100;
        IsFullyCharged = true;
        SetBarFill();
    }

    /// <summary>
    /// Charges the bar by one ChargingIncrement
    /// </summary>
    public void ChargeBar()
    {
        PercentCharged += math.min(100, ChargingIncrement);
        if (PercentCharged == 100)
        {
            Ship.Level.CancelTimer(_chargeBarTimer);
            //CancelInvoke(nameof(ChargeBar));
            IsCharging = false;
            IsFullyCharged = true;
        }
        //Debug.Log($"{Ship.Name} charge bar is charged {PercentCharged}%");
        SetBarFill();
    }

    /// <summary>
    /// Updates the visual appearance of the charging bar to match the PercentCharged
    /// </summary>
    public void SetBarFill()
    {
        BarFiller.transform.localScale = new Vector2(PercentCharged / 100.0f, BarFiller.transform.localScale.y);
    }
    private ScaledTimer _chargeBarTimer = new ScaledTimer();
    /// <summary>
    /// Drains the bar by the amount specified
    /// </summary>
    /// <param name="percent"></param>
    public void DrainBar(int percent = 100)
    {
        PercentCharged -= percent;
        IsFullyCharged = false;
        SetBarFill();
        if (!IsCharging)
        {
            _chargeBarTimer.Reuse(.5f, ChargeBar, true);
            Ship.Level.AddTimer(_chargeBarTimer);
            //InvokeRepeating(nameof(ChargeBar), 0, .5f);
            IsCharging = true;
        }
    }


}
