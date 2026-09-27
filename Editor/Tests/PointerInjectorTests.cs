using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    public class PointerInjectorTests
    {
        [Test]
        public void UnavailableInjector_ExplainsItselfAndNeverThrows()
        {
            var injector = new InputSystemPointerInjector(typeResolver: _ => null);
            Assert.IsFalse(injector.Available);
            StringAssert.Contains("no injectable input backend", injector.Unavailable);
            Assert.DoesNotThrow(() => injector.Press(new UnityEngine.Vector2(1, 1)));
            Assert.IsFalse(injector.Press(new UnityEngine.Vector2(1, 1)));
        }
    }
}
