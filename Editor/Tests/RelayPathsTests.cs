using System.IO;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class RelayPathsTests
    {
        private string _root = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "novakit-relaypaths-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void ResolveRoot_DefaultsToLibrarySoTheStudioRepoStaysClean()
        {
            var resolved = RelayPaths.ResolveRoot(_root);
            Assert.AreEqual(Path.Combine(_root, "Library", "AdRelay"), resolved);
        }

        [Test]
        public void ResolveRoot_KeepsALegacyAdRelaySession()
        {
            var legacy = Path.Combine(_root, RelayPaths.RootDirName);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "status.json"), "{}");
            Assert.AreEqual(legacy, RelayPaths.ResolveRoot(_root));
        }

        [Test]
        public void ResolveRoot_PrefersLibraryOnceThatSessionExists()
        {
            var preferred = Path.Combine(_root, "Library", "AdRelay");
            var legacy = Path.Combine(_root, RelayPaths.RootDirName);
            Directory.CreateDirectory(preferred);
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(preferred, "status.json"), "{}");
            File.WriteAllText(Path.Combine(legacy, "status.json"), "{}");
            Assert.AreEqual(preferred, RelayPaths.ResolveRoot(_root));
        }

        [Test]
        public void EnsureDirs_CreatesTheResolvedTree()
        {
            RelayPaths.EnsureDirs(_root);
            Assert.IsTrue(Directory.Exists(RelayPaths.Commands(_root)));
            Assert.IsTrue(RelayPaths.Commands(_root).Replace('\\', '/').Contains("Library/AdRelay"));
        }
    }
}
