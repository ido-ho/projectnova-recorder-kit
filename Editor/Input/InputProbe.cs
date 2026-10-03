using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ProjectNova.RecorderKit
{
    /// <summary>
    /// Read-only. Answers "could the kit drive this point, and if not, why not" — the question that
    /// went unasked until a gem-match3 shot armed a booster it could never fire.
    /// </summary>
    public static class InputProbe
    {
        public static IEnumerable<string> Report(float? x, float? y)
        {
            var lines = new List<string>();
            var es = EventSystem.current;
            lines.Add(es == null
                ? "EventSystem: NONE — nothing in the scene can receive pointer input at all"
                : $"EventSystem: {es.name}");

            var raycasters = Object.FindObjectsByType<BaseRaycaster>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            lines.Add(raycasters.Length == 0
                ? "raycasters: NONE"
                : "raycasters: " + string.Join(", ",
                    raycasters.Select(r => $"{r.GetType().Name}({r.name})")));
            var hasPhysics = raycasters.Any(r =>
                r.GetType().Name is "PhysicsRaycaster" or "Physics2DRaycaster");
            lines.Add($"physicsRaycaster: {(hasPhysics ? "yes — world colliders join the EventSystem graph" : "no — world-space objects are NOT hit-testable")}");

            var inputSystem = GameReflection.FindType("UnityEngine.InputSystem.InputSystem")
                              ?? GameReflection.FindType("InputSystem");
            lines.Add($"inputSystem package: {(inputSystem != null ? "present" : "ABSENT — legacy UnityEngine.Input only, which has no injection point")}");

            var hit = false;
            var handler = false;
            if (es != null && x.HasValue && y.HasValue)
            {
                var ped = new PointerEventData(es) { position = new Vector2(x.Value, y.Value) };
                var results = new List<RaycastResult>();
                es.RaycastAll(ped, results);
                hit = results.Count > 0;
                lines.Add(hit
                    ? $"at ({x},{y}) hits: " + string.Join(" | ",
                        results.Select(r => $"{r.gameObject.name}[{r.module?.GetType().Name}]"))
                    : $"at ({x},{y}): NOTHING hit");
                if (hit)
                {
                    var h = ExecuteEvents.GetEventHandler<IPointerClickHandler>(results[0].gameObject);
                    handler = h != null;
                    // Test `h`, not `handler`: the nullable analysis does not carry the bool's meaning
                    // across, and this assembly compiles CS8602 as an error.
                    lines.Add(h != null
                        ? $"click handler: {h.name}"
                        : "click handler: NONE up the parent chain — this point BLOCKS but does not respond");
                }
            }
            else if (!x.HasValue || !y.HasValue)
            {
                lines.Add("no point given — tier below reflects the SCENE's capability, not a pixel");
            }
            else
            {
                // A point WAS given and could not be tested, because there is no EventSystem to
                // raycast through. Caught live on gem-match3 during a level transition, where
                // EventSystem.current is briefly null: the old single else-branch said "no point
                // given" here, blaming the caller for the scene's state. Missing point and missing
                // EventSystem are different failures and must not share a message.
                lines.Add($"at ({x},{y}): NOT TESTED — there is no EventSystem to raycast through");
            }

            var tier = InputTier.Classify(es != null, hit, handler, hasPhysics, inputSystem != null);
            lines.Add($"tier: {InputTier.Name(tier)}");
            return lines;
        }
    }
}
