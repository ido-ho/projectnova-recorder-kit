using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>Plan v3.1 phase 4.10 (RL fix list 19) — the kit's menu shows a studio only what a studio presses.</summary>
    public class KitMenusTests
    {
        [Test]
        public void RunAllShots_IsNotAMenuItem_WithoutTheDeveloperDefine_StopStillIs()
        {
            var run = typeof(KitMenus).GetMethod(nameof(KitMenus.RunAllShots), BindingFlags.Public | BindingFlags.Static)!;
#if NOVA_KIT_DEV
            Assert.IsNotEmpty(run.GetCustomAttributes(typeof(MenuItem), false));
#else
            Assert.IsEmpty(run.GetCustomAttributes(typeof(MenuItem), false), "a studio's menu shows Run All Shots");
#endif
            var stop = typeof(KitMenus).GetMethod(nameof(KitMenus.Stop), BindingFlags.Public | BindingFlags.Static)!;
            Assert.IsNotEmpty(stop.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>().ToArray(), "control: Stop stays");
        }
    }
}
