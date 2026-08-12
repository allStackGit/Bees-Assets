using NUnit.Framework;
using Assets.Scripts;

namespace Bees.Tests.EditMode
{
    [TestFixture]
    [Category("BeesFoundation")]
    public class DevelopmentServerEndpointTests
    {
        [Test]
        public void DevelopmentClientUsesBeesServerDevelopmentPort()
        {
            Assert.That(ConfigData.DevelopmentPort, Is.EqualTo(7146));
            Assert.That(ConfigData.DevelopmentServerHostname, Is.EqualTo(ConfigData.LocalServerHostname));
        }
    }
}
