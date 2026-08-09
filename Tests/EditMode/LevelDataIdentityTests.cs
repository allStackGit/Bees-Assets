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
            Type levelDataType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelData");
            Type levelOptionsType = RuntimeAssembly.GetType("Assets.Scripts.Data.LevelOptions");
            object levelData = FormatterServices.GetUninitializedObject(levelDataType);

            Type listType = typeof(List<>).MakeGenericType(levelOptionsType);
            IList levels = (IList)Activator.CreateInstance(listType);
            levels.Add(Activator.CreateInstance(levelOptionsType, new object[] { 42, 1, "Forty Two" }));
            levels.Add(Activator.CreateInstance(levelOptionsType, new object[] { 7, 1, "Seven" }));

            FieldInfo levelsField = levelDataType.GetField("_levels", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(levelsField, Is.Not.Null);
            levelsField.SetValue(levelData, levels);

            object selected = RuntimeAssembly.Invoke(levelData, "GetLevel", 7);

            Assert.That(RuntimeAssembly.GetField(selected, "Id"), Is.EqualTo(7));
            Assert.That(RuntimeAssembly.GetField(selected, "Name"), Is.EqualTo("Seven"));
        }
    }
}
