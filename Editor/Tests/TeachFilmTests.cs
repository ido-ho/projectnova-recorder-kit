using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ProjectNova.RecorderKit.Tests
{
    /// <summary>Plan v3.1 phase 4.1 — SHOW ME ONCE, FILMED: the claim names the shot the demonstration films, and the teach's
    /// saved state keeps the film across an editor reload (the upload happens after it).</summary>
    public class TeachFilmTests
    {
        private static JObject Claim(object? filmShot) => new()
        {
            ["teach"] = new JObject
            {
                ["ask"] = "show me the summon",
                ["declaredStart"] = new JObject { ["kind"] = "lobby-after-boot" },
                ["authoredNames"] = new JArray(),
                ["filmShot"] = filmShot == null ? null : JToken.FromObject(filmShot),
            },
        };

        [Test]
        public void TheClaimNamesTheShotToFilm_OnlyAShotNameShape_AbsentIsPressesOnly()
        {
            Assert.AreEqual("lobby-pet-summon", TeachRequest.FromClaim(Claim("lobby-pet-summon").ToString())!.FilmShot);
            Assert.IsNull(TeachRequest.FromClaim(Claim(null).ToString())!.FilmShot);
            Assert.IsNull(TeachRequest.FromClaim(Claim("../../etc/passwd").ToString())!.FilmShot, "a path is never a shot name");
            Assert.IsNull(TeachRequest.FromClaim(Claim(42).ToString())!.FilmShot);
        }

        [Test]
        public void TheFilmSurvivesAReload_RequestAndClip()
        {
            var req = TeachRequest.FromClaim(Claim("lobby-pet-summon").ToString())!;
            var s = new TeachState(req) { FilmStartedUtc = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc), FilmClip = "/p/Library/Nova/Recordings/lobby-pet-summon_001.mp4" };
            var back = TeachState.FromJson(JToken.Parse(s.ToJson().ToString()))!;
            Assert.AreEqual("lobby-pet-summon", back.Request.FilmShot);
            Assert.AreEqual(s.FilmClip, back.FilmClip);
            Assert.AreEqual(s.FilmStartedUtc, back.FilmStartedUtc);
            // control: a teach with no film keeps none
            var plain = TeachState.FromJson(JToken.Parse(new TeachState(TeachRequest.FromClaim(Claim(null).ToString())!).ToJson().ToString()))!;
            Assert.IsNull(plain.Request.FilmShot);
            Assert.IsNull(plain.FilmClip);
        }
    }
}
