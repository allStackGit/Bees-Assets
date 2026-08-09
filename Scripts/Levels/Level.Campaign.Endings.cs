using Assets.Scripts.Data;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using static Assets.Scripts.ConfigData;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        public void Pluto1Ending(SavedSquad gunshipSquad)
        {
            Debug.Log("Level complete!");
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 3);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 1);
            State.PlayerNewShipsReceived += 4;

            FleetShip gunship = CurrentShips.GetFirstAvailableShipOfType(ConfigData.ShipTypes.Gunship);
            gunshipSquad.AddShipToSquad(new SquadShip(gunship.Id, gunship.Type, Vector2.zero));
            gunshipSquad.AutoRepositionSquad();

            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Gunship, 2);
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Scout, 1);

            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Honeybee);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Hornet);
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelEndedDialogue();
        }

        public void Pluto2Ending()
        {
            Debug.Log("Level 1 complete!");
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 1);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 3);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 3);
            State.PlayerNewShipsReceived += 7;

            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Dreadnought, 3);
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Frigate, 3);
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Scout, 1);

            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Hornet);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Dreadnought);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Frigate);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Dreadnought);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Wasp);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.YellowJacket);
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Pluto3Ending()
        {
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2);
            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 1);
            State.PlayerNewShipsReceived += 3;

            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Wasp);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Leafcutter);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.YellowJacket);
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Pluto4Ending()
        {
            switch (_questPoints)
            {
                case 60:
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 8);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 4);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2);
                    State.PlayerNewShipsReceived += 19;
                    break;
                case > 50:
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 6);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Dreadnought, 2);
                    State.PlayerNewShipsReceived += 15;
                    break;
                case > 35:
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Frigate, 2);
                    State.PlayerNewShipsReceived += 11;
                    break;
                case > 15:
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 4);
                    State.PlayerNewShipsReceived += 9;
                    break;
                default:
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Scout, 5);
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Gunship, 1);
                    State.PlayerNewShipsReceived += 6;
                    break;
            }

            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;

            if (!ConfigData.UserProgressData.IsHumanFreePlayUnlocked)
            {
                ConfigData.UserProgressData.IsHumanFreePlayUnlocked = true;
                Dialogue trainingRoomAlert = null;
                trainingRoomAlert = new Dialogue(
                    Stage.DialoguePrefab,
                    "New Game Mode!",
                    "You've unlocked the Training Room!",
                    new List<string>() { "Ok" },
                    new List<UnityAction>()
                    {
                        () =>
                        {
                            trainingRoomAlert.Hide();
                            Pluto4EndingDialogue();
                        }
                    });
                trainingRoomAlert.Show();
            }
            else
            {
                Pluto4EndingDialogue();
            }

            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Leafcutter);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.YellowJacket);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.CarpenterBee);
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();
        }

        public void Pluto4EndingDialogue()
        {
            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Neptune1Ending()
        {
            Debug.Log("Level 3 complete");
            if (WinningSide == ConfigData.Configuration.UserSide)
            {
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Factory, 5);
                ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
                ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
                ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Factory);
                State.PlayerNewShipsReceived += 5;
            }
            else
            {
                ConfigData.UserProgressData.AdvanceToNextLevel();
                int mineralsMined = 0;
                for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
                {
                    _save_savedSquad = AllSquads[_save_i];
                    if (_save_savedSquad.Side != ConfigData.Configuration.BeeSide)
                    {
                        continue;
                    }

                    _save_savedSquad.GetSquadShips().ForEach(ship =>
                    {
                        _save_fleetship = ship.GetFleetShip();
                        mineralsMined += _save_fleetship.MineralsMinedThisLevel;
                        _save_fleetship.MineralsMinedThisLevel = 0;
                    });
                }

                int bumblebees = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Bumblebee).Tsv;
                mineralsMined %= ConfigData.GetShipInfo(ConfigData.ShipTypes.Bumblebee).Tsv;
                int leafcutters = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Leafcutter).Tsv;
                mineralsMined %= ConfigData.GetShipInfo(ConfigData.ShipTypes.Leafcutter).Tsv;
                int hornets = mineralsMined / ConfigData.GetShipInfo(ConfigData.ShipTypes.Hornet).Tsv;

                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Hornet, hornets);
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Leafcutter, leafcutters);
                ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Bumblebee, bumblebees);
            }

            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.CarpenterBee);
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Neptune2Ending()
        {
            Debug.Log("Level 4 complete");
            CollectMinedMineralsForPlayer();
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore + State.PlayerMineralsReceived;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();
            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Neptune3Ending()
        {
            Debug.Log("Level 5 complete");
            if (WinningSide == ConfigData.Configuration.AISide)
            {
                CurrentShips.GetFleetShips()
                    .Where(fleetShip => fleetShip.Type == ConfigData.ShipTypes.Factory)
                    .ToList()
                    .ForEach(fleetShip => fleetShip.IsDead = true);
            }

            ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Carrier, 1);
            CurrentShips.BuildNewSquad($"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}", ConfigData.Configuration.HumanSide, ShipTypes.Carrier, 1);
            State.PlayerNewShipsReceived += 1;
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;
            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = true;
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Uranus1Ending()
        {
            Debug.Log("Level 6 complete");
            if (WinningSide == ConfigData.Configuration.AISide || !ConfigData.CurrentShips.HasShipsOfType(ConfigData.ShipTypes.Factory))
            {
                ConfigData.UserProgressData.AdvanceToNextLevel();
            }

            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Cruiser);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Uranus2Ending()
        {
            CollectMinedMineralsForPlayer();
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore + State.PlayerMineralsReceived;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();
            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        public void Uranus3Ending()
        {
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Barge);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Barge);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Barge);

            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
            ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.WarpGate);
            ConfigData.UserProgressData.VisibleCodexBeeShipTypes.Add(ConfigData.ShipTypes.Beehive);

            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Factory);
            ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.WarpGate);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Flagship);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.FireBarge);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier);
            ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.WarpGate);
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Beehive);
            ConfigData.UserProgressData.SetShipTypes();

            ConfigData.UserProgressData.IsBeeFreePlayUnlocked = true;
            ConfigData.UserProgressData.IsHumanChallengeUnlocked = true;
            ConfigData.UserProgressData.IsFishTankUnlocked = true;
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        private void CollectMinedMineralsForPlayer()
        {
            for (_save_i = 0; _save_i < AllSquads.Count; _save_i++)
            {
                _save_savedSquad = AllSquads[_save_i];
                _save_savedSquad.GetSquadShips().ForEach(ship =>
                {
                    _save_fleetship = ship.GetFleetShip();
                    _save_fleetship.MineralsMined += _save_fleetship.MineralsMinedThisLevel;
                    ConfigData.UserProgressData.MinedTSV += _save_fleetship.MineralsMinedThisLevel;
                    State.PlayerMineralsReceived += _save_fleetship.MineralsMinedThisLevel;
                    _save_fleetship.MineralsMinedThisLevel = 0;
                });
            }
        }

        private void SaveCampaignProgress()
        {
            ConfigData.UserProgressData.Save();
            CurrentShips.SaveSquadData();
            CurrentShips.SaveFleetData();
        }
    }
}
