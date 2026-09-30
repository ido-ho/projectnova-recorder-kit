using NUnit.Framework;

/// <summary>
/// Kit 0.14.3 (deep review KIT-2, invariant 192) — THE TESTS' FIXTURE TYPES ARE "THE GAME". A <c>call</c> or <c>set</c>
/// reaches only a game assembly now (<see cref="ProjectNova.RecorderKit.ReflectionScope"/>), and the test assembly's name
/// (<c>ProjectNova.RecorderKit…</c>) is one the rule refuses — so every suite that drives a fixture type through the bridge
/// runs with this assembly named runnable, set once before any test. In the GLOBAL namespace on purpose: a
/// <c>[SetUpFixture]</c> covers only its own namespace, and the fixtures live in several (NovaFixtures.*,
/// IngameDebugConsole, MonopolyFixture.Save). <c>ReflectionScopeTests.TheTestAssemblyItselfIsRefusedWithoutTheSeam</c>
/// takes it away to prove the rule still refuses it.
/// </summary>
[SetUpFixture]
public class KitTestRunScope
{
    [OneTimeSetUp]
    public void NameTheTestAssemblyRunnable() =>
        ProjectNova.RecorderKit.ReflectionScope.AlsoRunnableForTests.Add(typeof(KitTestRunScope).Assembly);

    [OneTimeTearDown]
    public void Forget() =>
        ProjectNova.RecorderKit.ReflectionScope.AlsoRunnableForTests.Remove(typeof(KitTestRunScope).Assembly);
}

/// <summary>
/// Kit 0.14.3 (KIT-2) — the suites' commands name a game class <c>Player</c> (<c>set Player.coins 5</c>) that no real
/// game supplies in the test host, where the bare name would otherwise resolve to a Unity package's own
/// <c>Unity.PerformanceTesting.Data.Player</c> — and the tick would be refused, correctly, as outside the game. In a real
/// project <c>Player</c> is the game's class; this global-namespace one stands in for it (a global type's full name IS its
/// bare name, so it wins the lookup) and lives in the assembly the one-time setup names runnable.
/// </summary>
public class Player
{
}
