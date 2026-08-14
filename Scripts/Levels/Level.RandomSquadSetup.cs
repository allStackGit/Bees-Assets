using Assets.Scripts.Data;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private readonly List<SavedSquad> _randomSquadBuffer = new List<SavedSquad>();
        private int _randomQueenCount;

        private void SetupShipsForSide(int side)
        {
            bool generateRandomSquads = Stage.IsTrainingNueralNetwork ||
                                        Stage.UseFullyRandomSquads ||
                                        ((Stage.UseFullyRandomEnemySquads || CurrentLevelOptions.EnemySquadGenerationCount > 0) &&
                                         side == ConfigData.Configuration.AISide);

            if (generateRandomSquads)
            {
                AddRandomSquadsForSetup(side);
            }
            else if ((Stage.UseOverrideSquads && side == ConfigData.Configuration.UserSide) ||
                     (Stage.UseOverrideEnemySquads && side == ConfigData.Configuration.AISide))
            {
                LevelConstructor.AddOverrideSquads(side);
            }

            if (side == ConfigData.Configuration.AISide)
            {
                List<int> existingSquadIds = CurrentLevelOptions.EnemyExistingSquads;
                for (int i = 0; i < existingSquadIds.Count; i++)
                {
                    SavedSquad existingSquad = ConfigData.CurrentShips.GetSavedSquad(existingSquadIds[i]);
                    if (existingSquad != null)
                    {
                        CurrentLevelOptions.EnemySquads.Add(existingSquad);
                    }
                }
                LevelConstructor.SpawnShipsAndSquads(
                    CurrentLevelOptions.EnemySquads,
                    StartingPositions[side - 1],
                    Vector2.zero,
                    false);
            }
            else
            {
                LevelConstructor.SpawnShipsAndSquads(
                    CurrentLevelOptions.ChosenSquads,
                    StartingPositions[side - 1],
                    Vector2.zero,
                    false);
            }
        }

        private void AddRandomSquadsForSetup(int side)
        {
            _randomQueenCount = 0;
            bool noVisibleArmedTypes = HasNoVisibleArmedTypes(side);
            List<ConfigData.ShipTypes> availableTypes = side == ConfigData.Configuration.HumanSide
                ? Stage.HumanShipTypes
                : Stage.BeeShipTypes;

            for (int option = 0; option < (ActivateLoadingShipsMidLevel ? 2 : 1); option++)
            {
                bool hasArmedSquads = false;
                _randomSquadBuffer.Clear();

                for (int i = 0; i < CurrentLevelOptions.EnemySquadGenerationCount; i++)
                {
                    ConfigData.ShipTypes type = availableTypes[Random.Range(0, availableTypes.Count)];
                    while (side == ConfigData.Configuration.BeeSide &&
                           type == ConfigData.ShipTypes.Queen &&
                           availableTypes.Count > 1 &&
                           (HasObstacles || _randomQueenCount == 2 || Utilities.RandomInt(4) != 3))
                    {
                        type = availableTypes[Random.Range(0, availableTypes.Count)];
                    }

                    long squadId = Utilities.GetNegativeSavedSquadId();
                    SavedSquad savedSquad = new SavedSquad(
                        squadId,
                        side,
                        $"{type}s #{squadId}",
                        Vector2.zero,
                        false,
                        false,
                        ConfigData.DefaultShootingStrategy,
                        ConfigData.UnsetColor,
                        null);
                    savedSquad.SetupRandomShips(type);
                    _randomSquadBuffer.Add(savedSquad);

                    if (type == ConfigData.ShipTypes.Queen)
                    {
                        _randomQueenCount++;
                    }

                    if (ConfigData.ArmedShipTypes.Contains(type) ||
                        (side == ConfigData.Configuration.BeeSide && Stage.OverrideBeeShipTypes.Count > 0) ||
                        (side == ConfigData.Configuration.HumanSide && Stage.OverrideHumanShipTypes.Count > 0) ||
                        CurrentLevelOptions.EnemyShipTypeOption != 0 ||
                        noVisibleArmedTypes)
                    {
                        hasArmedSquads = true;
                    }

                    if (i == CurrentLevelOptions.EnemySquadGenerationCount - 1 && !hasArmedSquads)
                    {
                        i--;
                        _randomSquadBuffer.RemoveAt(_randomSquadBuffer.Count - 1);
                    }
                }

                if (option == 0)
                {
                    if (side == ConfigData.Configuration.AISide)
                    {
                        CurrentLevelOptions.EnemySquads.AddRange(_randomSquadBuffer);
                    }
                    else
                    {
                        CurrentLevelOptions.ChosenSquads.AddRange(_randomSquadBuffer);
                    }
                }
                else if (side == ConfigData.Configuration.AISide)
                {
                    CurrentLevelOptions.EnemyReinforcements.AddRange(_randomSquadBuffer);
                }
            }
        }

        private static bool HasNoVisibleArmedTypes(int side)
        {
            if (side == ConfigData.Configuration.BeeSide)
            {
                foreach (ConfigData.ShipTypes type in ConfigData.UserProgressData.VisibleBeeShipTypes)
                {
                    if (ConfigData.ArmedShipTypes.Contains(type))
                    {
                        return false;
                    }
                }
                return true;
            }

            foreach (ConfigData.ShipTypes type in ConfigData.UserProgressData.VisibleHumanShipTypes)
            {
                if (ConfigData.ArmedShipTypes.Contains(type))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
