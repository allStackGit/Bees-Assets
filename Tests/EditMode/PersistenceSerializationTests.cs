using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;
using System.IO;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class PersistenceSerializationTests
    {
        private CultureInfo _originalCulture;
        private Type _configDataType;
        private object _originalConfiguration;
        private readonly List<string> _temporaryDirectories = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _originalCulture = CultureInfo.CurrentCulture;
            _configDataType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData");
            _originalConfiguration = RuntimeAssembly.GetStaticField(_configDataType, "Configuration");
        }

        [TearDown]
        public void TearDown()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", _originalConfiguration);
            foreach (string directory in _temporaryDirectories)
            {
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        File.Delete(file);
                    }
                    Directory.Delete(directory);
                }
            }
            _temporaryDirectories.Clear();
        }

        [Test]
        public void EmptyFleetSerializesAsValidEmptyArray()
        {
            object fleet = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetData");
            SetFieldIncludingBase(fleet, "_shipList", CreateList("Assets.Scripts.Data.FleetShip"));

            string json = (string)RuntimeAssembly.Invoke(fleet, "ToJson");

            Assert.That(json, Is.EqualTo("[]"));
            AssertValidJson(json);
        }

        [Test]
        public void EmptySavedSquadSerializesWithoutRemovingStructuralCharacters()
        {
            object squad = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.SavedSquad");
            SetFieldIncludingBase(squad, "Id", 12L);
            SetFieldIncludingBase(squad, "Side", 1);
            SetFieldIncludingBase(squad, "Name", "Empty squad");
            SetFieldIncludingBase(squad, "Color", Color.white);
            SetFieldIncludingBase(squad, "StartingPosition", new Vector2(1.25f, -2.5f));
            SetFieldIncludingBase(squad, "ChosenShootingStrategy", Enum.Parse(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShootingStrategyTypes"), "FirstSeen"));
            SetFieldIncludingBase(squad, "Stats", Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.SquadStatBlock"),
                new object[] { "Commander", 0, 0, 0, 0, 0, 0 }));
            SetFieldIncludingBase(squad, "_ships", CreateList("Assets.Scripts.Data.SquadShip"));

            string json = (string)RuntimeAssembly.Invoke(squad, "ToJson");

            AssertValidJson(json);
            Assert.That(json, Does.Contain("\"Ships\":[]"));
        }

        [Test]
        public void FleetShipNameIsEscapedAsJsonData()
        {
            object ship = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.FleetShip");
            SetFieldIncludingBase(ship, "Id", 44L);
            SetFieldIncludingBase(ship, "Name", "Queen \"A\"\\Reserve\nLine");
            SetFieldIncludingBase(ship, "Type", Enum.ToObject(
                RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes"), 1));

            string json = (string)RuntimeAssembly.Invoke(ship, "ToJson");

            AssertValidJson(json);
            Assert.That(json, Does.Contain("\\\"A\\\"").And.Contain("\\\\Reserve").And.Contain("\\nLine"));
        }

        [Test]
        public void LevelJsonIsValidUnderCommaDecimalCultureAndEscapesNames()
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            object level = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions"),
                new object[] { 3, 2, "Mission \"Quoted\"" });
            IList obstacleList = (IList)RuntimeAssembly.GetField(level, "ObstacleList");
            obstacleList.Add(((Vector2, Vector2))(new Vector2(1.25f, 2.5f), new Vector2(3.75f, 4.5f)));

            string json = (string)RuntimeAssembly.Invoke(level, "ToJson");

            AssertValidJson(json);
            Assert.That(json, Does.Contain("1.25").And.Not.Contain("1,25"));
            Assert.That(json, Does.Contain("Mission \\\"Quoted\\\""));
        }

        [Test]
        public void MalformedReplacementDoesNotOverwritePreviouslyValidDataFileContents()
        {
            object file = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.DataFile");
            const string valid = "{\"Version\":1}";
            RuntimeAssembly.Invoke(file, "SetContents", valid);

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(file, "SetContents", "{\"Version\":"));
            Assert.That(exception.InnerException, Is.Not.Null);
            Assert.That(RuntimeAssembly.Invoke(file, "GetContents"), Is.EqualTo(valid));
            Assert.That(RuntimeAssembly.Invoke(file, "GetJsonObject"), Is.Not.Null);
        }

        [TestCase(true, false, true, 0, TestName = "LocalPrimaryWritesOnlyLocal")]
        [TestCase(true, true, true, 1, TestName = "LocalPrimaryMirrorWritesBoth")]
        [TestCase(false, false, false, 1, TestName = "ServerPrimaryWritesOnlyServer")]
        [TestCase(false, true, true, 1, TestName = "ServerPrimaryMirrorWritesBoth")]
        public void DataFileRoutesWritesAccordingToStorageContract(
            bool useLocalStorage,
            bool mirrorStorage,
            bool expectsLocalFile,
            int expectedServerWrites)
        {
            InstallStorageConfiguration(useLocalStorage, mirrorStorage);
            string directory = CreateTemporaryDirectory();
            var serverWrites = new List<string>();
            object file = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.DataFile"),
                new object[] { "routing", directory, 77UL, new Action<string>(serverWrites.Add) });
            const string json = "{\"Version\":1,\"Value\":\"sync\"}";

            RuntimeAssembly.Invoke(file, "WriteData", json);

            string fullPath = (string)RuntimeAssembly.GetField(file, "FullPath");
            Assert.That(File.Exists(fullPath), Is.EqualTo(expectsLocalFile));
            if (expectsLocalFile)
            {
                Assert.That(File.ReadAllText(fullPath), Is.EqualTo(json));
            }
            Assert.That(serverWrites, Has.Count.EqualTo(expectedServerWrites));
            if (expectedServerWrites > 0)
            {
                Assert.That(serverWrites[0], Is.EqualTo(json));
            }
        }

        [Test]
        public void MalformedWriteHasNoLocalOrServerSideEffectsAndPreservesLastGoodValue()
        {
            InstallStorageConfiguration(useLocalStorage: true, mirrorStorage: true);
            string directory = CreateTemporaryDirectory();
            var serverWrites = new List<string>();
            object file = Activator.CreateInstance(
                RuntimeAssembly.GetType("Assets.Scripts.Data.DataFile"),
                new object[] { "atomic", directory, 88UL, new Action<string>(serverWrites.Add) });
            const string valid = "{\"Version\":1}";
            RuntimeAssembly.Invoke(file, "WriteData", valid);
            string fullPath = (string)RuntimeAssembly.GetField(file, "FullPath");

            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
                RuntimeAssembly.Invoke(file, "WriteData", "{\"Version\":"));

            Assert.That(exception.InnerException, Is.Not.Null);
            Assert.That(File.ReadAllText(fullPath), Is.EqualTo(valid));
            Assert.That(serverWrites, Is.EqualTo(new[] { valid }));
            Assert.That(RuntimeAssembly.Invoke(file, "GetContents"), Is.EqualTo(valid));
        }

        [Test]
        public void FullUserProgressDocumentEscapesStringsUsesJsonBooleansAndStableShipOrdering()
        {
            object progress = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.UserProgressData");
            RuntimeAssembly.SetField(progress, "PlayerName", "Pilot \"Bee\"\\One\nLine");
            RuntimeAssembly.SetField(progress, "HasStartedHumanCampaign", true);
            RuntimeAssembly.SetField(progress, "ShowToolTips", false);
            Type shipType = RuntimeAssembly.GetType("Assets.Scripts.ConfigData+ShipTypes");
            RuntimeAssembly.SetField(progress, "VisibleBeeShipTypes", CreateSet(shipType, "Honeybee"));
            RuntimeAssembly.SetField(progress, "VisibleHumanShipTypes", CreateSet(shipType, "Gunship", "Scout"));
            RuntimeAssembly.SetField(progress, "VisibleCodexBeeShipTypes", CreateSet(shipType));
            RuntimeAssembly.SetField(progress, "VisibleCodexHumanShipTypes", CreateSet(shipType, "Scout"));
            RuntimeAssembly.SetField(progress, "UnlockedCampaignShips", CreateSet(shipType, "Gunship", "Scout"));

            string json = (string)RuntimeAssembly.Invoke(progress, "ToJson");

            AssertValidJson(json);
            Assert.That(json, Does.Contain("\"HasStartedHumanCampaign\":true"));
            Assert.That(json, Does.Contain("\"ShowToolTips\":false"));
            Assert.That(json, Does.Not.Contain("\"HasStartedHumanCampaign\":\"True\""));
            Assert.That(json, Does.Contain("Pilot \\\"Bee\\\"\\\\One\\nLine"));
            Assert.That(json.IndexOf("Gunship", StringComparison.Ordinal),
                Is.LessThan(json.IndexOf("Scout", StringComparison.Ordinal)));
        }

        private static object CreateList(string elementTypeName)
        {
            return Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(
                RuntimeAssembly.GetType(elementTypeName)));
        }

        private static object CreateSet(Type enumType, params string[] names)
        {
            object set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(enumType));
            foreach (string name in names)
            {
                RuntimeAssembly.AddToCollection(set, Enum.Parse(enumType, name));
            }
            return set;
        }

        private void InstallStorageConfiguration(bool useLocalStorage, bool mirrorStorage)
        {
            object configuration = RuntimeAssembly.CreateUninitialized(
                "Assets.Scripts.Settings.Configuration");
            RuntimeAssembly.SetField(configuration, "UseLocalStorage", useLocalStorage);
            RuntimeAssembly.SetField(configuration, "MirrorStorage", mirrorStorage);
            RuntimeAssembly.SetStaticField(_configDataType, "Configuration", configuration);
        }

        private string CreateTemporaryDirectory()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string root = Path.Combine(projectRoot, "Temp", "PersistenceContractTests");
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _temporaryDirectories.Add(directory);
            return directory;
        }

        private static void AssertValidJson(string json)
        {
            object file = RuntimeAssembly.CreateUninitialized("Assets.Scripts.Data.DataFile");
            Assert.DoesNotThrow(() => RuntimeAssembly.Invoke(file, "SetContents", json), json);
            Assert.That(RuntimeAssembly.Invoke(file, "GetJsonObject"), Is.Not.Null);
        }

        private static void SetFieldIncludingBase(object instance, string fieldName, object value)
        {
            Type type = instance.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return;
                }
                type = type.BaseType;
            }
            throw new MissingFieldException(instance.GetType().FullName, fieldName);
        }
    }
}
