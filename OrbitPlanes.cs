using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Data;
using Game.Info;
using HarmonyLib;
using Manager;
using PlanetsScripts;
using UnityEngine;
using UnityEngine.SceneManagement;

using static TiltedWorlds.Plugin;
using static TiltedWorlds.BodyData;
using static TiltedWorlds.BodyOrientation;
using static TiltedWorlds.Magnetosphere;
using static TiltedWorlds.MagnetosphereGeometry;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    internal static class OrbitPlanes
    {
        internal static void ReapplyMoonPlanes()
        {
            OrbitUniversal[] orbits = Resources.FindObjectsOfTypeAll<OrbitUniversal>();
            for (int i = 0; i < orbits.Length; i++)
            {
                OrbitUniversal orbit = orbits[i];
                if (orbit == null || !MoonOrbits.TryGetValue(orbit.gameObject.name, out MoonOrbit moon))
                {
                    continue;
                }

                try
                {
                    if (!TryGetMoonOrbitElements(moon, out double inclination, out double node, out _))
                    {
                        continue;
                    }

                    orbit.inclination = inclination;
                    orbit.omega_uc = node;
                    // Saved state vectors would otherwise overwrite the new plane in Init().
                    orbit.Reset();
                    orbit.Init();
                }
                catch (Exception error)
                {
                    ReportError($"Could not rebuild the plane of '{orbit.gameObject.name}'", error);
                }
            }
        }

        /// <summary>
        /// The LowOrbit points are written in the InitNBody prefix, which may run before the frame
        /// offset is known. Rebuild them once it is. This is the same rebuild the optional #5 patch
        /// performs, but invoked once from here instead of on every Init().
        /// </summary>
        internal static void ReapplyLowOrbitPlanes()
        {
            OrbitUniversal[] orbits = Resources.FindObjectsOfTypeAll<OrbitUniversal>();
            for (int i = 0; i < orbits.Length; i++)
            {
                OrbitUniversal orbit = orbits[i];
                if (orbit == null)
                {
                    continue;
                }

                try
                {
                    RebuildOrbitInEquator(orbit, orbit.gameObject.name);
                }
                catch (Exception error)
                {
                    ReportError($"Could not rebuild the plane of '{orbit.gameObject.name}'", error);
                }
            }
        }

        internal static bool TryGetMoonOrbitElements(MoonOrbit moon, out double inclination, out double node, out Vector3 normal)
        {
            // Every source plane, including an ecliptic one, needs the real-to-game longitude
            // offset. Do not write a provisional plane while Earth's frame is still unknown.
            if (!TryGetFrameOffsetCached(out double frameOffset))
            {
                inclination = 0.0;
                node = 0.0;
                normal = Vector3.forward;
                return false;
            }

            CalculateMoonOrbitElements(moon, frameOffset, out inclination, out node, out normal);
            return true;
        }

        /// <summary>Rotate a J2000 ecliptic moon plane into the game's ecliptic frame.</summary>
        internal static void CalculateMoonOrbitElements(MoonOrbit moon, double frameOffset,
            out double inclination, out double node, out Vector3 normal)
        {
            inclination = moon.Inclination;
            node = ((moon.AscendingNode + frameOffset) % 360.0 + 360.0) % 360.0;
            normal = GameOrbitNormal(inclination, node);
        }

        /// <summary>Orbit normal used by OrbitUniversal: (sin O sin i, -cos O sin i, cos i).</summary>
        internal static Vector3 GameOrbitNormal(double inclination, double node)
        {
            double i = inclination * DegToRad;
            double o = node * DegToRad;
            return new Vector3(
                (float)(Math.Sin(o) * Math.Sin(i)),
                (float)(-Math.Cos(o) * Math.Sin(i)),
                (float)Math.Cos(i));
        }

        /// <summary>Inverse of <see cref="GameOrbitNormal"/>: the elements of a plane with this normal.</summary>
        private static void PoleToOrbitElements(Vector3 normal, out double inclination, out double node)
        {
            normal.Normalize();
            // atan2 retains the small horizontal components; acos(float z) loses precision
            // near an equatorial plane and distorts the recovered small moon inclinations.
            double sinInclination = Math.Sqrt((double)normal.x * normal.x + (double)normal.y * normal.y);
            inclination = Math.Atan2(sinInclination, normal.z) * RadToDeg;
            if (sinInclination < 1e-6)
            {
                node = 0.0;
                return;
            }

            node = Math.Atan2(normal.x, -normal.y) * RadToDeg;
            if (node < 0.0)
            {
                node += 360.0;
            }
        }

        /// <summary>
        /// Puts a body's LowOrbit pseudo-object into the body's equatorial plane.
        /// The game creates one of these per body ("LowOrbitEarth", ...) and uses it as the
        /// endpoint of planned missions, so this is what makes a transfer target the tilted
        /// equator instead of the authored ecliptic plane.
        /// </summary>
        internal static void TryTiltOrbitObject(OrbitUniversal orbit, string name)
        {
            if (!TryGetOrbitObjectPole(orbit, name, out Vector3 pole))
            {
                return;
            }

            // Asteroids being pulled carry a LowOrbit object of their own, but it holds no usable
            // elements — writing a plane into it only skews whatever it is used for.
            if (orbit.p_inspector <= 0.0 || orbit.eccentricity >= 1.0)
            {
                return;
            }

            PoleToOrbitElements(pole, out double inclination, out double node);

            orbit.inclination = inclination;
            orbit.omega_uc = node;
        }

        /// <summary>
        /// Rebuilds a LowOrbit object around the body's real pole when its plane came
        /// from a state vector instead of the authored elements. OrbitUniversal remembers a saved
        /// state as r/v plus an internal initFromRVT flag, and Init() then derives the elements
        /// from that state, so writing <c>inclination</c> alone is silently discarded. Resetting
        /// to element mode and re-initialising keeps the same semi-major axis, eccentricity and
        /// phase, and changes only the plane.
        /// </summary>
        private static void RebuildOrbitInEquator(OrbitUniversal orbit, string name)
        {
            if (!TryGetOrbitObjectPole(orbit, name, out Vector3 pole))
            {
                return;
            }

            // An orbit that has not been initialised yet has no meaningful elements to rebuild.
            if (orbit.p_inspector <= 0.0 || orbit.eccentricity >= 1.0)
            {
                return;
            }

            PoleToOrbitElements(pole, out double inclination, out double node);

            if (Math.Abs(orbit.inclination - inclination) < 1e-6
                && Math.Abs(AngleDelta(orbit.omega_uc, node)) < 1e-6)
            {
                return;
            }

            orbit.inclination = inclination;
            orbit.omega_uc = node;
            orbit.Reset();
            orbit.Init();
        }

        /// <summary>Body, and the pole its orbit points should follow, for a LowOrbit object.</summary>
        private static bool TryGetOrbitObjectPole(OrbitUniversal orbit, string name, out Vector3 pole)
        {
            pole = Vector3.forward;

            if (name == null || !name.StartsWith("LowOrbit", StringComparison.Ordinal))
            {
                return false;
            }

            ObjectInfo body = ResolveOrbitObjectBody(orbit);
            if (body == null || body.NBody == null)
            {
                return false;
            }

            // Only the body's own point: the game centres it on the body's NBody. Pulled asteroids
            // get a LowOrbit centred on another orbit object ("center=LowOrbitMars"), and writing a
            // plane there skews whatever that object drives.
            if (orbit.centerNbody != body.NBody)
            {
                return false;
            }

            string key = KeyOf(body);
            return key != null && TryGetAxisTarget(key, body, out pole);
        }

        /// <summary>Signed-free difference between two angles in degrees, in [-180, 180].</summary>
        private static double AngleDelta(double from, double to)
        {
            double delta = (to - from) % 360.0;
            if (delta > 180.0)
            {
                delta -= 360.0;
            }
            else if (delta < -180.0)
            {
                delta += 360.0;
            }
            return delta;
        }

        /// <summary>
        /// The body an orbit pseudo-object belongs to. CreateOrbit parents the object to that body,
        /// so the parent chain is authoritative. The orbit's centre is only a fallback: asteroids
        /// being pulled also have a LowOrbit object, but it hangs off the asteroid while its centre
        /// is the planet it is towed to, and those must not be re-planed.
        /// </summary>
        private static ObjectInfo ResolveOrbitObjectBody(OrbitUniversal orbit)
        {
            ObjectInfo body = orbit.GetComponentInParent<ObjectInfo>();

            if (body == null && orbit.centerNbody != null)
            {
                body = orbit.centerNbody.GetObjectInfo();
            }

            return body != null && IsBody(body) ? body : null;
        }

        /// <summary>
        /// Parked ships are drawn on a decorative "PointsOnOrbits" ring. Its ObjectInfo graph is
        /// unreliable (a Uranus ship can point at Earth), so the body is resolved from the name of
        /// the transform the ring hangs under. The ring's plane is taken from the prefab's own
        /// frame and turned so its normal follows the body's real pole. It must not be inferred
        /// from the ships' current positions: the game gives every ship an independent random
        /// phase and inclination, so two positions do not define the plane they orbit in.
        /// </summary>
        internal static void AlignOrbitRings()
        {
            SpawnPoinObjectOnOrbit[] rings = Resources.FindObjectsOfTypeAll<SpawnPoinObjectOnOrbit>();

            var nodes = new List<Transform>();
            var axes = new Dictionary<Transform, Vector3>();

            for (int i = 0; i < rings.Length; i++)
            {
                SpawnPoinObjectOnOrbit ring = rings[i];
                if (ring == null)
                {
                    continue;
                }

                Transform node = ring.transform.parent;
                if (node == null || node.name != "PointsOnOrbits")
                {
                    continue;
                }

                if (axes.ContainsKey(node))
                {
                    continue;
                }

                // The prefab root holds the authored orbit frame. The game only writes a local
                // Y rotation on it, which leaves its up unchanged, so this axis is stable across
                // repeated calls. RotationZ (a child) carries the random +-10 degree inclination
                // and RotationY the random phase, so neither may be used here: their up already
                // includes that per-ship tilt.
                nodes.Add(node);
                axes.Add(node, ring.transform.up);
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                Transform node = nodes[i];
                if (node == null || node.parent == null)
                {
                    continue;
                }

                string key = KeyFromTransform(node.parent);
                if (key == null || !TryGetAxisTarget(key, null, out Vector3 pole))
                {
                    continue;
                }

                pole.Normalize();

                Vector3 axis = axes[node];
                if (axis.sqrMagnitude < 1e-8f)
                {
                    continue;
                }

                axis.Normalize();
                if (Vector3.Dot(axis, pole) < 0f)
                {
                    // A plane normal is sign-agnostic; keep the shortest rotation.
                    axis = -axis;
                }

                if (Vector3.Dot(axis, pole) > 0.999999f)
                {
                    // Already aligned: leave the ring untouched so repeated passes (another
                    // ship being created) cannot drift it by accumulated rounding.
                    continue;
                }

                node.rotation = Quaternion.FromToRotation(axis, pole) * node.rotation;
            }
        }

        /// <summary>Body pole for a transform, resolved by walking up its names.</summary>
    }

    [HarmonyPatch(typeof(OrbitUniversal), nameof(OrbitUniversal.InitNBody))]
    internal static class OrbitPlaneInitPatch
    {
        private static void Prefix(OrbitUniversal __instance)
        {
            string name = null;
            try
            {
                name = __instance.gameObject.name;

                // Moon orbits use paired JPL inclination/node in their published reference plane.
                if (!BodyData.MoonOrbits.TryGetValue(name, out BodyData.MoonOrbit moon))
                {
                    // A body's own LowOrbit point follows its tilted equator.
                    OrbitPlanes.TryTiltOrbitObject(__instance, name);
                    return;
                }

                if (!OrbitPlanes.TryGetMoonOrbitElements(moon, out double inclination, out double node, out _))
                {
                    // The scene is not ready yet (the real-to-game frame offset is unknown).
                    // ReapplyMoonPlanes runs once it is.
                    return;
                }

                // Only the orbital plane is changed. Eccentricity, semi-major axis and the
                // argument of periapsis stay exactly as authored by the game.
                __instance.inclination = inclination;
                __instance.omega_uc = node;
            }
            catch (Exception error)
            {
                // Never let a failure escape into OrbitUniversal.InitNBody.
                Plugin.ReportError($"Could not set the orbital plane of '{name ?? "<unknown>"}'", error);
            }
        }
    }




    /// <summary>
    /// The parked-ship ring is built lazily when the first ship reaches the orbit, so align it
    /// right after the game creates it. Alignment is idempotent, so a ship created on one body
    /// leaves the rings of every other body untouched.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfo), nameof(ObjectInfo.CreateObjectOnOrbit))]
    internal static class OrbitPointsPatch
    {
        private static void Postfix()
        {
            try
            {
                OrbitPlanes.AlignOrbitRings();
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not align the orbit points ring", error);
            }
        }
    }

    /// <summary>
    /// The game spreads pulled asteroids around their ring by rotating the fake about its local
    /// Z, which only kept them in the ecliptic while that axis was the ecliptic normal. Repeat
    /// that rotation about the body's real pole instead, so a pulled asteroid stays in the
    /// body's equatorial plane.
    /// </summary>
    [HarmonyPatch(typeof(FakeObjectRotateAsRealObject), "ChangePosition")]
    internal static class AsteroidFakeRotationPatch
    {
        private static void Postfix(FakeObjectRotateAsRealObject __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }

                if (__instance.faceAttractor && __instance.name == "MoonFake")
                {
                    try { StabilizeMoonView(__instance); }
                    catch (Exception error) { Plugin.ReportError("Could not stabilize the Moon's orientation", error); }
                }

                if (!__instance.fakeRotation.HasValue) return;

                Transform parent = __instance.transform.parent;
                if (parent == null || parent.parent == null)
                {
                    return;
                }

                if (!BodyOrientation.TryGetPoleForTransform(__instance.transform, out Vector3 pole))
                {
                    return;
                }

                Vector3 axis = parent.parent.InverseTransformDirection(pole).normalized;
                if (axis.sqrMagnitude < 1e-8f)
                {
                    return;
                }

                parent.localRotation = Quaternion.AngleAxis(__instance.fakeRotation.Value, axis);
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not re-orient a pulled asteroid", error);
            }
        }

        private static readonly ConditionalWeakTable<FakeObjectRotateAsRealObject, RotateObject> MoonViews =
            new ConditionalWeakTable<FakeObjectRotateAsRealObject, RotateObject>();

        /// <summary>The RotateObject the game spins under a fake; resolved once per fake object.</summary>
        private static RotateObject ResolveMoonView(FakeObjectRotateAsRealObject fake)
        {
            if (MoonViews.TryGetValue(fake, out RotateObject view) && view != null)
            {
                return view;
            }

            MoonViews.Remove(fake);
            view = fake.GetComponentInChildren<RotateObject>(true);
            if (view != null)
            {
                MoonViews.Add(fake, view);
            }

            return view;
        }

        private static void StabilizeMoonView(FakeObjectRotateAsRealObject fake)
        {
            OrbitUniversal orbit = fake.orbitUniversal;
            if (orbit == null || orbit.centerNbody == null
                || !BodyOrientation.TryGetAxisTarget("Moon", null, out Vector3 pole)) return;

            Vector3 towardEarth = orbit.centerNbody.transform.position - fake.transform.position;
            if (towardEarth.sqrMagnitude < 1e-10f) return;
            towardEarth.Normalize();
            Vector3 north = Vector3.ProjectOnPlane(pole, towardEarth);
            if (north.sqrMagnitude < 1e-10f) return;
            north.Normalize();

            // The vanilla right-vector setter chooses an unstable roll near the opposite
            // direction. Keep local +X toward Earth and local +Z close to the Moon's pole.
            fake.transform.rotation = Quaternion.LookRotation(north, Vector3.Cross(north, towardEarth));

            // The orbit and spin axes differ slightly; keep the visible body's pole exact.
            RotateObject view = ResolveMoonView(fake);
            if (view == null)
            {
                return;
            }
            Vector3 spin = BodyOrientation.GetSpinAxis(view);
            if (spin.sqrMagnitude > 1e-8f)
                view.transform.rotation = Quaternion.FromToRotation(spin, pole) * view.transform.rotation;
        }
    }
}
