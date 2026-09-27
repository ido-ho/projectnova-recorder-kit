using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>
    /// The classifier is pure so the RULE can be argued about without an editor. Each case below is a
    /// real configuration seen on an onboarded game, named in the test so a future change has to
    /// explain which real game it is breaking.
    /// </summary>
    public class InputTierTests
    {
        [Test] // roguelegend: uGUI buttons, no input-system package. Everything it needs is reachable.
        public void UguiWidget_IsUgui()
        {
            Assert.AreEqual(InputTierKind.Ugui, InputTier.Classify(
                hasEventSystem: true, hitSomething: true, hasClickHandler: true,
                hasPhysicsRaycaster: false, inputSystemPresent: false));
        }

        [Test] // gem-match3's board: nothing under the point handles clicks, but the backend is injectable.
        public void NoHandler_WithInputSystem_IsBackend()
        {
            Assert.AreEqual(InputTierKind.Backend, InputTier.Classify(
                hasEventSystem: true, hitSomething: true, hasClickHandler: false,
                hasPhysicsRaycaster: false, inputSystemPresent: true));
        }

        [Test] // the honest dead end: legacy input, non-uGUI content, no public injection point.
        public void NoHandler_NoInputSystem_IsUnreachable()
        {
            Assert.AreEqual(InputTierKind.Unreachable, InputTier.Classify(
                hasEventSystem: true, hitSomething: true, hasClickHandler: false,
                hasPhysicsRaycaster: false, inputSystemPresent: false));
        }

        [Test] // a point over empty space still tells us about the backend, not about this pixel.
        public void NothingHit_FallsBackToBackendCapability()
        {
            Assert.AreEqual(InputTierKind.Backend, InputTier.Classify(
                hasEventSystem: true, hitSomething: false, hasClickHandler: false,
                hasPhysicsRaycaster: false, inputSystemPresent: true));
        }

        [Test] // a PhysicsRaycaster means world-space colliders DO join the EventSystem graph.
        public void PhysicsRaycaster_WithHandler_IsUgui()
        {
            Assert.AreEqual(InputTierKind.Ugui, InputTier.Classify(
                hasEventSystem: true, hitSomething: true, hasClickHandler: true,
                hasPhysicsRaycaster: true, inputSystemPresent: true));
        }

        [Test] // no EventSystem at all is a global failure and must not be reported as a tier verdict.
        public void NoEventSystem_IsUnreachableRegardlessOfBackend()
        {
            Assert.AreEqual(InputTierKind.Unreachable, InputTier.Classify(
                hasEventSystem: false, hitSomething: false, hasClickHandler: false,
                hasPhysicsRaycaster: false, inputSystemPresent: true));
        }

        [Test]
        public void Names_AreTheExactFourStringsTheSchemaUses()
        {
            Assert.AreEqual("ugui", InputTier.Name(InputTierKind.Ugui));
            Assert.AreEqual("backend", InputTier.Name(InputTierKind.Backend));
            Assert.AreEqual("seam", InputTier.Name(InputTierKind.Seam));
            Assert.AreEqual("unreachable", InputTier.Name(InputTierKind.Unreachable));
        }
    }
}
