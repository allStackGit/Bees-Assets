using Assets.Scripts;
using Assets.Scripts.Levels;
using System.Collections.Generic;
using UnityEngine;

public static class StageConfigOptions
{
    public static void Apply(Stage stage, Level level)
    {
        if (stage.TimeoutTime == 0)
        {
            stage.TimeoutTime = int.MaxValue;
        }

        stage.TimeScale = stage.OverrideTimeScale == 0
            ? ConfigData.Configuration.TimeScale
            : stage.OverrideTimeScale;
        Time.timeScale = stage.TimeScale;

        if (stage.GeneratedSquadCountOverride > 0)
        {
            level.CurrentLevelOptions.EnemySquadGenerationCount = stage.GeneratedSquadCountOverride;
        }
        if (level.CurrentLevelOptions.EnemySquadGenerationCount > 0)
        {
            level.CurrentLevelOptions.EnemySquadGenerationCount =
                Utilities.RandomInt(level.CurrentLevelOptions.EnemySquadGenerationCount - stage.GeneratedSquadCountMinimum) +
                1 + stage.GeneratedSquadCountMinimum;
        }

        RefillBeeTypes(stage);
        RefillHumanTypes(stage);

        int enemyShipTypeOption = level.CurrentLevelOptions.EnemyShipTypeOption;
        if (enemyShipTypeOption == -1)
        {
            if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
            {
                KeepOnly(stage.BeeShipTypes, Utilities.RandomInt(stage.BeeShipTypes.Count));
                Debug.Log($"The user has selected randomized enemy ship type: {stage.BeeShipTypes[0]}");
            }
            else
            {
                KeepOnly(stage.HumanShipTypes, Utilities.RandomInt(stage.HumanShipTypes.Count));
                Debug.Log($"The user has selected randomized enemy ship type: {stage.HumanShipTypes[0]}");
            }
        }
        else if (enemyShipTypeOption == 0)
        {
            // The lists already contain the same override/default sources that the legacy
            // Stage implementation redundantly rebuilt in this branch.
        }
        else if (ConfigData.Configuration.AISide == ConfigData.Configuration.BeeSide)
        {
            KeepOnly(stage.BeeShipTypes, enemyShipTypeOption - 1);
        }
        else
        {
            KeepOnly(stage.HumanShipTypes, enemyShipTypeOption - 1);
            Debug.Log($"The user has selected enemy ship type: {stage.HumanShipTypes[0]}");
        }
    }

    private static void RefillBeeTypes(Stage stage)
    {
        if (stage.OverrideBeeShipTypes.Count > 0)
        {
            Refill(ref stage.BeeShipTypes, stage.OverrideBeeShipTypes);
        }
        else
        {
            Refill(ref stage.BeeShipTypes, ConfigData.BeeShipTypes);
        }
    }

    private static void RefillHumanTypes(Stage stage)
    {
        if (stage.OverrideHumanShipTypes.Count > 0)
        {
            Refill(ref stage.HumanShipTypes, stage.OverrideHumanShipTypes);
        }
        else
        {
            Refill(ref stage.HumanShipTypes, ConfigData.HumanShipTypes);
        }
    }

    private static void Refill(ref List<ConfigData.ShipTypes> destination, IEnumerable<ConfigData.ShipTypes> source)
    {
        if (ReferenceEquals(destination, source))
        {
            destination = new List<ConfigData.ShipTypes>(source);
            return;
        }

        destination.Clear();
        destination.AddRange(source);
    }

    private static void KeepOnly(List<ConfigData.ShipTypes> shipTypes, int index)
    {
        ConfigData.ShipTypes selected = shipTypes[index];
        shipTypes.Clear();
        shipTypes.Add(selected);
    }
}
