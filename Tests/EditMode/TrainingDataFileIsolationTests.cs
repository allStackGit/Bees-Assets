using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class TrainingDataFileIsolationTests
    {
        [Test]
        public void DedicatedTrainingSuppressesAllDataFilePersistence()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));

            int writeData = source.IndexOf("public object WriteData(string data)");
            int trainingGuard = source.IndexOf(
                "HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime", writeData);
            int localWrite = source.IndexOf("WriteLocalData(data);", writeData);
            int serverWrite = source.IndexOf("WriteServerData(data);", writeData);

            Assert.That(trainingGuard, Is.GreaterThan(writeData));
            Assert.That(trainingGuard, Is.LessThan(localWrite));
            Assert.That(trainingGuard, Is.LessThan(serverWrite),
                "Training must return before either local or server persistence side effects.");
        }

        [Test]
        public void MissingTrainingFileAcceptsTransientDefaultInsteadOfRetryLoop()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath, "Scripts", "Data", "DataFile.cs"));

            int failedResponse = source.IndexOf("standingRequest.Status == -1");
            int trainingGuard = source.IndexOf(
                "HiveMindTrainingBootstrap.IsDedicatedTrainingRuntime && _isDataLoaded", failedResponse);
            int retry = source.IndexOf("ReadContents();", failedResponse);

            Assert.That(trainingGuard, Is.GreaterThan(failedResponse));
            Assert.That(trainingGuard, Is.LessThan(retry),
                "A transient in-memory training default must be accepted before the normal resend path.");
        }
    }
}
