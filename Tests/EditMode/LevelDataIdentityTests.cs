using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class LevelDataIdentityTests
    {
        [Test]
        public void GetLevelUsesPersistedIdInsteadOfListPosition()
        {
            object levelData = CreateLevelDataWithIds(42, 7);

            object selected = RuntimeAssembly.Invoke(levelData, "GetLevel", 7);

            Assert.That(RuntimeAssembly.GetField(selected, "Id"), Is.EqualTo(7));
            Assert.That(RuntimeAssembly.GetField(selected, "Name"), Is.EqualTo("Level 7"));
        }

        [Test]
        public void GetNewIdUsesHighestPersistedIdInsteadOfCount()
        {
            object levelData = CreateLevelDataWithIds(42, 7);

            Assert.That(RuntimeAssembly.Invoke(levelData, "GetCurrentId"), Is.EqualTo(42));
            Assert.That(RuntimeAssembly.Invoke(levelData, "GetNewId"), Is.EqualTo(43));
        }

        private static object CreateLevelDataWithIds(params int[] ids)
        {
            Type levelDataType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelData");
            Type levelOptionsType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions");
            object levelData = FormatterServices.GetUninitializedObject(levelDataType);

            Type listType = typeof(List<>).MakeGenericType(levelOptionsType);
            IList levels = (IList)Activator.CreateInstance(listType);
            foreach (int id in ids)
            {
                levels.Add(Activator.CreateInstance(levelOptionsType, new object[] { id, 1, $"Level {id}" }));
            }

            FieldInfo levelsField = levelDataType.GetField("_levels", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(levelsField, Is.Not.Null);
            levelsField.SetValue(levelData, levels);
            return levelData;
        }
    }
}
