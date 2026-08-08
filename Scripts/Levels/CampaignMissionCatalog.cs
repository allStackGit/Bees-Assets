using System;
using System.Collections.Generic;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Authoritative mapping between persisted campaign level IDs and their runtime setup methods.
    /// Keep campaign data, intro data, and tests aligned with this catalog.
    /// </summary>
    public static class CampaignMissionCatalog
    {
        public enum AutomatedScenarioStatus
        {
            Ready,
            InDevelopment,
            MissingPersistedData
        }

        public sealed class MissionDefinition
        {
            public readonly int Id;
            public readonly string Name;
            public readonly string SetupMethod;
            public readonly string CompletionMethod;
            public readonly string TerminalMethod;
            public readonly bool HasPersistedLevelData;
            public readonly AutomatedScenarioStatus ScenarioStatus;
            private readonly Action<Level> _configure;

            public MissionDefinition(int id, string name, string setupMethod, string completionMethod,
                string terminalMethod, Action<Level> configure,
                bool hasPersistedLevelData = true,
                AutomatedScenarioStatus scenarioStatus = AutomatedScenarioStatus.Ready)
            {
                Id = id;
                Name = name;
                SetupMethod = setupMethod;
                CompletionMethod = completionMethod;
                TerminalMethod = terminalMethod;
                _configure = configure;
                HasPersistedLevelData = hasPersistedLevelData;
                ScenarioStatus = scenarioStatus;
            }

            public void Configure(Level level)
            {
                _configure(level);
            }
        }

        public static readonly IReadOnlyList<MissionDefinition> Definitions =
            new MissionDefinition[]
            {
                new MissionDefinition(0, "Anomaly", nameof(Level.Pluto1Anomaly), nameof(Level.Pluto1Ending), nameof(Level.Pluto1Ending), level => level.Pluto1Anomaly()),
                new MissionDefinition(1, "Reinforcements", nameof(Level.Pluto2Reinforcements), nameof(Level.Pluto2Ending), nameof(Level.Pluto2Ending), level => level.Pluto2Reinforcements()),
                new MissionDefinition(2, "Pushback", nameof(Level.Pluto3Pushback), nameof(Level.Pluto3Ending), nameof(Level.Pluto3Ending), level => level.Pluto3Pushback()),
                new MissionDefinition(3, "Bluer Pastures", nameof(Level.Pluto4BluerPastures), nameof(Level.Pluto4Ending), nameof(Level.Pluto4EndingDialogue), level => level.Pluto4BluerPastures()),
                new MissionDefinition(4, "Seize the Means", nameof(Level.Neptune1SeizeTheMeans), nameof(Level.Neptune1Ending), nameof(Level.Neptune1Ending), level => level.Neptune1SeizeTheMeans()),
                new MissionDefinition(5, "Of Production", nameof(Level.Neptune2OfProduction), nameof(Level.Neptune2Ending), nameof(Level.Neptune2Ending), level => level.Neptune2OfProduction()),
                new MissionDefinition(6, "Pressing Forward", nameof(Level.Neptune3PressingForwardCampaign), nameof(Level.Neptune3Ending), nameof(Level.Neptune3Ending), level => level.Neptune3PressingForwardCampaign()),
                new MissionDefinition(7, "Minesweeper", nameof(Level.Titania1MinesweeperCampaign), nameof(Level.Titania1MinesweeperEnding), nameof(Level.Titania1MinesweeperEnding), level => level.Titania1MinesweeperCampaign(), true, AutomatedScenarioStatus.InDevelopment),
                new MissionDefinition(8, "Beenoculars", nameof(Level.Titania2BeenocularsCampaign), nameof(Level.Titania2Ending), nameof(Level.Titania2Ending), level => level.Titania2BeenocularsCampaign(), true, AutomatedScenarioStatus.InDevelopment),
                // These trigger graphs exist, but campaign_levels.json currently stops at mission 8.
                new MissionDefinition(9, "On the Offensive", nameof(Level.Uranus1OnTheOffensive), nameof(Level.Uranus1Ending), nameof(Level.Uranus1Ending), level => level.Uranus1OnTheOffensive(), false, AutomatedScenarioStatus.MissingPersistedData),
                new MissionDefinition(10, "On the Defensive", nameof(Level.Uranus2OnTheDefensive), nameof(Level.Uranus2Ending), nameof(Level.Uranus2Ending), level => level.Uranus2OnTheDefensive(), false, AutomatedScenarioStatus.MissingPersistedData),
                new MissionDefinition(11, "A New Threat", nameof(Level.Uranus3ANewThreat), nameof(Level.Uranus3Ending), nameof(Level.Uranus3Ending), level => level.Uranus3ANewThreat(), false, AutomatedScenarioStatus.MissingPersistedData),
            };

        public static IEnumerable<MissionDefinition> GetAutomatedScenarioDefinitions()
        {
            for (int index = 0; index < Definitions.Count; index++)
            {
                MissionDefinition definition = Definitions[index];
                if (definition.HasPersistedLevelData &&
                    definition.ScenarioStatus == AutomatedScenarioStatus.Ready)
                {
                    yield return definition;
                }
            }
        }

        public static MissionDefinition Get(int id)
        {
            if (id >= 0 && id < Definitions.Count && Definitions[id].Id == id)
            {
                return Definitions[id];
            }

            for (int index = 0; index < Definitions.Count; index++)
            {
                if (Definitions[index].Id == id)
                {
                    return Definitions[index];
                }
            }

            throw new ArgumentOutOfRangeException(nameof(id), id,
                "No campaign mission setup is registered for this level ID.");
        }

        public static void Configure(Level level, int id)
        {
            if (level == null)
            {
                throw new ArgumentNullException(nameof(level));
            }

            Get(id).Configure(level);
        }
    }
}
