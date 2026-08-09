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

        /// <summary>
        /// Titania mission 2: defend Titania while A.M.I. and the base personnel evacuate.
        /// Bee tactical target selection remains server/Hive Mind driven; this method owns
        /// only the authored battle cadence, objective lifecycle, dialogue and completion.
        /// </summary>
        public void Titania2BeenocularsCampaign()
        {
            const float survivalDuration = 450f;

            _titania2Resolved = false;
            _titania2MissionTimers.Clear();
            HasContinuousTriggers = true;
            Stage.Menus.SetMissionStatus("Survive and defend Titania!");
            Stage.CutsceneManager.Setup(Titania2CampaignEnding);

            // Index 0 is the pre-mission briefing shown by LevelIntro. The authored in-level
            // opening is indices 1-10. Index 11 is conditional dialogue for a failed
            // Minesweeper outcome; there is not yet a reliable persisted outcome flag for it.
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
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(12, 3));
                    }
                    if (!playedTwentyFourPercent && uploadProgress >= 0.24f)
                    {
                        playedTwentyFourPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(15, 3));
                    }
                    if (!playedFiftyPercent && uploadProgress >= 0.50f)
                    {
                        playedFiftyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(18, 3));
                    }
                    if (!playedSeventyFivePercent && uploadProgress >= 0.75f)
                    {
                        playedSeventyFivePercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(21, 3));
                    }
                    if (!playedNinetyPercent && uploadProgress >= 0.90f)
                    {
                        playedNinetyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(24, 2));
                    }

                    int minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                    int secondsLeft = Mathf.FloorToInt(timeLeft % 60f);
                    clockText.text = $"{minutesLeft}:{secondsLeft:D2}";

                    // This is a survival objective. Clearing the current Bee force does not
                    // end the mission because later waves are part of the evacuation pressure.
                    if (timeLeft <= 0f)
                    {
                        ResolveTitania2(titania, true);
                    }
                }, true);
                AddTitania2Timer(survivalClock);

                // Initial pressure establishes that Titania can be approached from every side.
                // These are map-relative entry lanes rather than world coordinates; the old
                // draft used +/-340 to +/-420 on a 512-unit map and reinforcement placement
                // intentionally bypasses PositionSquads' normal bounds correction.
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

                // Continue pressure through the second half instead of leaving a four-minute lull.
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

        private void AddTitania2BeeWave(List<SavedSquad> squads, float normalizedX, float normalizedY)
        {
            Vector2 spawnPoint = FindTitania2SpawnPoint(normalizedX, normalizedY);
            // Hive Mind/server behavior owns the destination. Passing Vector2.zero prevents
            // the client from issuing an unnecessary pre-Hive-Mind move command.
            AddReinforcementSquads(squads, spawnPoint, Vector2.zero);
        }

        /// <summary>
        /// Resolves a desired edge lane to a point that is inside the current map and not
        /// immediately inside authored obstacle geometry. normalizedX/Y are -1..1 where
        /// +/-1 represent the corresponding map edge.
        /// </summary>
        private Vector2 FindTitania2SpawnPoint(float normalizedX, float normalizedY)
        {
            const float boundaryInset = 32f;
            const float obstacleClearance = 32f;
            const float scanStep = 24f;
            const int maxScanSteps = 8;

            float xT = (Mathf.Clamp(normalizedX, -1f, 1f) + 1f) * 0.5f;
            float yT = (Mathf.Clamp(normalizedY, -1f, 1f) + 1f) * 0.5f;
            Vector2 min = new Vector2(MinX + boundaryInset, MinY + boundaryInset);
            Vector2 max = new Vector2(MaxX - boundaryInset, MaxY - boundaryInset);
            Vector2 desired = new Vector2(
                Mathf.Lerp(min.x, max.x, xT),
                Mathf.Lerp(min.y, max.y, yT));

            Physics2D.SyncTransforms();
            Vector2 candidate = desired;
            for (int step = 0; step <= maxScanSteps; step++)
            {
                if (Physics2D.OverlapCircle(candidate, obstacleClearance, ConfigData.ObstaclesLayerMask) == null)
                {
                    return candidate;
                }

                candidate = Vector2.MoveTowards(candidate, Titania2Center, scanStep);
                candidate.x = Mathf.Clamp(candidate.x, min.x, max.x);
                candidate.y = Mathf.Clamp(candidate.y, min.y, max.y);
            }

            // The authored Map starting position is the safest fallback because it is intended
            // to receive a fleet. Prefer it to knowingly spawning a reinforcement in a wall.
            Vector2 fallback = StartingPositions[ConfigData.Configuration.AISide - 1];
            if (Physics2D.OverlapCircle(fallback, obstacleClearance, ConfigData.ObstaclesLayerMask) == null)
            {
                Debug.LogWarning($"Beenoculars entry lane {normalizedX},{normalizedY} was blocked; using AI starting position {fallback}.");
                return fallback;
            }

            Debug.LogWarning($"Beenoculars could not find a fully clear reinforcement entry near {desired}; using the in-bounds candidate {candidate}.");
            return candidate;
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
            WinningSide = success
                ? ConfigData.Configuration.UserSide
                : ConfigData.Configuration.AISide;

            CloseLevel();
            Stage.CutsceneManager.PlayDialogueSection(
                success
                    ? Stage.CutsceneManager.Titania_Beenoculars.GetRange(31, 2)
                    : Stage.CutsceneManager.Titania_Beenoculars.GetRange(26, 5),
                true);
        }

        /// <summary>
        /// Persist Titania 2 just like the completed campaign missions. Both success and
        /// retreat/failure continue the campaign; the mission outcome remains available on
        /// the Level through WinningSide/DidUserWin for result presentation and follow-up.
        /// </summary>
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
