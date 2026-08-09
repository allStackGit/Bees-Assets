using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private static readonly Vector2 Titania2Center = new Vector2(-32f, 55f);
        private bool _titania2Resolved;
        private readonly List<ScaledTimer> _titania2MissionTimers = new List<ScaledTimer>();

        public void Titania2BeenocularsCampaign()
        {
            const float survivalDuration = 450f;

            _titania2Resolved = false;
            _titania2MissionTimers.Clear();
            HasContinuousTriggers = true;

            // Campaign LevelOptions can carry an old/custom starting position. SetupMapAndCamera
            // applies that position before SetupShips, so translate the already-created human
            // formation back to Titania's authored fleet start before the first frame is shown.
            AlignTitania2HumanFleetToAuthoredStart();

            Stage.Menus.SetMissionStatus("Survive and defend Titania!");
            Stage.CutsceneManager.Setup(Titania2CampaignEnding);

            Stage.Menus.TogglePausePanel();
            Stage.CutsceneManager.PlayDialogueSection(
                Stage.CutsceneManager.Titania_Beenoculars.GetRange(1, 10));

            NextTriggers.Add(new Trigger(() => Stage.CutsceneManager.HitDialogueBreak, () =>
            {
                Stage.Menus.TogglePausePanel();
                Stage.EnablePlayerControl();

                HumanTarget titania = CreateHumanTarget(Titania2Center);

                TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                Stage.Menus.Clock.SetActive(true);
                float endTime = Time.time + survivalDuration;

                bool playedTenPercent = false;
                bool playedTwentyFourPercent = false;
                bool playedFiftyPercent = false;
                bool playedSeventyFivePercent = false;
                bool playedNinetyPercent = false;

                ScaledTimer survivalClock = new ScaledTimer();
                survivalClock.Reuse(1f, () =>
                {
                    if (_titania2Resolved)
                    {
                        return;
                    }

                    float timeLeft = Mathf.Max(0f, endTime - Time.time);
                    float uploadProgress = 1f - (timeLeft / survivalDuration);

                    if (!playedTenPercent && uploadProgress >= 0.10f)
                    {
                        playedTenPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(12, 3));
                    }
                    if (!playedTwentyFourPercent && uploadProgress >= 0.24f)
                    {
                        playedTwentyFourPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(15, 3));
                    }
                    if (!playedFiftyPercent && uploadProgress >= 0.50f)
                    {
                        playedFiftyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(18, 3));
                    }
                    if (!playedSeventyFivePercent && uploadProgress >= 0.75f)
                    {
                        playedSeventyFivePercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(21, 3));
                    }
                    if (!playedNinetyPercent && uploadProgress >= 0.90f)
                    {
                        playedNinetyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(24, 2));
                    }

                    int minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                    int secondsLeft = Mathf.FloorToInt(timeLeft % 60f);
                    clockText.text = $"{minutesLeft}:{secondsLeft:D2}";

                    if (timeLeft <= 0f)
                    {
                        ResolveTitania2(titania, true);
                    }
                }, true);
                AddTitania2Timer(survivalClock);

                // Bee forces deliberately spawn beyond the visible battlefield and fly through
                // an entry point just inside the corresponding edge. This avoids reinforcement
                // groups visibly popping into existence on the playable map.
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, 0.85f, 0.55f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                }, -0.85f, 0.65f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                }, 0.45f, -0.9f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                }, -0.45f, -0.9f);

                Stage.ActivateHiveMind = true;
                SetupHivemind();

                ScaledTimer wave1 = new ScaledTimer(60f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true)
                    }, 0.95f, 0f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave1);

                ScaledTimer wave2 = new ScaledTimer(120f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                    }, -0.95f, 0f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave2);

                ScaledTimer wave3 = new ScaledTimer(210f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true)
                    }, 0f, 0.95f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave3);

                ScaledTimer wave4 = new ScaledTimer(300f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true)
                    }, 0.75f, -0.95f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave4);

                ScaledTimer wave5 = new ScaledTimer(375f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true)
                    }, 0.8f, 0.85f);
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 3, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true)
                    }, -0.8f, -0.85f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave5);

                NextTriggers.Add(new Trigger(
                    () => titania.IsDead || State.IsSideKilled(ConfigData.Configuration.UserSide),
                    () => ResolveTitania2(titania, false),
                    "Titania 2 Losing condition"));

            }, "Titania 2 Start Level"));
        }

        private void AlignTitania2HumanFleetToAuthoredStart()
        {
            int userIndex = ConfigData.Configuration.UserSide - 1;
            Vector2 previousAnchor = StartingPositions[userIndex];
            Vector2 authoredAnchor = Map != null ? Map.UserStartingPosition : previousAnchor;
            Vector2 translation = authoredAnchor - previousAnchor;

            if (translation.sqrMagnitude > 0.01f)
            {
                foreach (Squad squad in State.GetSquadsBySide(ConfigData.Configuration.UserSide))
                {
                    foreach (Ship ship in squad.GetShips())
                    {
                        ship.transform.localPosition += (Vector3)translation;
                    }
                    squad.SetOffsets();
                }
                Physics2D.SyncTransforms();
                Debug.Log($"Moved Beenoculars human fleet from persisted anchor {previousAnchor} to Titania start {authoredAnchor}.");
            }

            CurrentLevelOptions.UserStartingPosition = authoredAnchor;
            StartingPositions[userIndex] = authoredAnchor;
            Stage.DefaultCameraPosition = authoredAnchor;
        }

        private void AddTitania2BeeWave(List<SavedSquad> squads, float normalizedX, float normalizedY)
        {
            if (squads == null || squads.Count == 0)
            {
                return;
            }

            const float laneSpread = 0.18f;
            bool horizontalEntry = Mathf.Abs(normalizedX) >= Mathf.Abs(normalizedY);

            for (int i = 0; i < squads.Count; i++)
            {
                float centeredIndex = i - ((squads.Count - 1) * 0.5f);
                float laneOffset = centeredIndex * laneSpread;
                float squadX = horizontalEntry ? normalizedX : normalizedX + laneOffset;
                float squadY = horizontalEntry ? normalizedY + laneOffset : normalizedY;

                GetTitania2OffMapEntry(squadX, squadY, out Vector2 spawnPoint, out Vector2 entryPoint);
                AddReinforcementSquads(new List<SavedSquad> { squads[i] }, spawnPoint, entryPoint);
            }
        }

        private void GetTitania2OffMapEntry(
            float normalizedX,
            float normalizedY,
            out Vector2 spawnPoint,
            out Vector2 entryPoint)
        {
            const float outsideDistance = 80f;
            const float insideDistance = 28f;
            const float tangentMargin = 48f;

            float xT = (Mathf.Clamp(normalizedX, -1f, 1f) + 1f) * 0.5f;
            float yT = (Mathf.Clamp(normalizedY, -1f, 1f) + 1f) * 0.5f;
            float tangentX = Mathf.Lerp(MinX + tangentMargin, MaxX - tangentMargin, xT);
            float tangentY = Mathf.Lerp(MinY + tangentMargin, MaxY - tangentMargin, yT);

            if (Mathf.Abs(normalizedX) >= Mathf.Abs(normalizedY))
            {
                bool fromRight = normalizedX >= 0f;
                spawnPoint = new Vector2(fromRight ? MaxX + outsideDistance : MinX - outsideDistance, tangentY);
                entryPoint = new Vector2(fromRight ? MaxX - insideDistance : MinX + insideDistance, tangentY);
            }
            else
            {
                bool fromTop = normalizedY >= 0f;
                spawnPoint = new Vector2(tangentX, fromTop ? MaxY + outsideDistance : MinY - outsideDistance);
                entryPoint = new Vector2(tangentX, fromTop ? MaxY - insideDistance : MinY + insideDistance);
            }
        }

        private void AddTitania2Timer(ScaledTimer timer)
        {
            _titania2MissionTimers.Add(timer);
            AddTimer(timer);
        }

        private void CancelTitania2Timers()
        {
            foreach (ScaledTimer timer in _titania2MissionTimers)
            {
                CancelTimer(timer);
            }
            _titania2MissionTimers.Clear();
        }

        private void ResolveTitania2(HumanTarget titania, bool survivedEvacuation)
        {
            if (_titania2Resolved)
            {
                return;
            }

            bool userAlive = !State.IsSideKilled(ConfigData.Configuration.UserSide);
            bool success = survivedEvacuation && titania != null && !titania.IsDead && userAlive;

            _titania2Resolved = true;
            CancelTitania2Timers();
            Stage.Menus.Clock.SetActive(false);
            WinningSide = success ? ConfigData.Configuration.UserSide : ConfigData.Configuration.AISide;

            CloseLevel();
            Stage.CutsceneManager.PlayDialogueSection(
                success
                    ? Stage.CutsceneManager.Titania_Beenoculars.GetRange(31, 2)
                    : Stage.CutsceneManager.Titania_Beenoculars.GetRange(26, 5),
                true);
        }

        public void Titania2CampaignEnding()
        {
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
    }
}
