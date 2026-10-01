using UnityEditor;
using UnityEngine;

namespace ProjectNova.RecorderKit
{
    public static class KitMenus
    {
        // Plan v3.1 phase 4.10 (RL fix list 19): "Run All Shots" sat beside "Nova Capture" and a studio asked which to
        // press. It runs every adapter shot at once with no start check and no upload — a kit developer's tool, so the
        // menu item exists only with the NOVA_KIT_DEV scripting define (Player Settings › Scripting Define Symbols).
#if NOVA_KIT_DEV
        [MenuItem("Tools/Recorder Kit/Developer/Run All Shots")]
#endif
        public static void RunAllShots()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[RecorderKit] Enter Play Mode before running.");
                return;
            }
            if (!AdapterRegistry.IsRegistered)
            {
                Debug.LogError("[RecorderKit] No GameAdapter registered — is the game's adapter on this branch?");
                return;
            }
            // Menu runs are fully autonomous — no agent in the loop to answer vision requests.
            AdDirector.Run(AdapterRegistry.Current, AdapterRegistry.Current.Shots(),
                new AdDirector.Options { SkipVisionShots = true });
        }

        [MenuItem("Tools/Recorder Kit/Stop")]
        public static void Stop() => AdDirector.Active?.Finish("stopped by user");
    }
}
