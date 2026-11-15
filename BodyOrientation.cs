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
using static TiltedWorlds.OrbitPlanes;
using static TiltedWorlds.Magnetosphere;
using static TiltedWorlds.MagnetosphereGeometry;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    internal static class BodyOrientation
    {
        /// <summary>
        /// Spin phase authored on the aurora prefab. Purely cosmetic: the caps are surfaces of
        /// revolution about the aurora's own axis, so only the tilt is visible.
        /// </summary>
        private static readonly Quaternion AuroraPhase = Quaternion.Euler(0f, 70f, 0f);
        internal static void TiltView(Scene scene, RotateObject rot)
        {
            if (rot == null)
            {
                return;
            }

            GameObject go = rot.gameObject;
            if (!go.scene.IsValid() || go.scene != scene)
            {
                return;
            }

            string key = ResolveBodyKey(rot, out ObjectInfo body);
            if (key == null || !TryGetAxisTarget(key, body, out Vector3 target))
            {
                return;
            }

            ApplyBodyTilt(key, rot, target);
            AlignAurora(rot.transform, key);
            AlignRings(body, target);
        }


        // Planets whose IAU north pole is the *lower* end of the axis: they rotate clockwise when
        // viewed from it, so the pole that points up and turns counter-clockwise is the opposite one.
        private static readonly HashSet<string> LowerNorthPole = new HashSet<string>(StringComparer.Ordinal)
        {
            "Venus",
            "Uranus",
        };

        /// <summary>
        /// Rotates the visual model so the end of its axis that points up and turns counter-clockwise
        /// (the game's spin axis) lands on the body's real pole. A view owned by a fake object with
        /// <c>faceAttractor</c> is re-oriented by the game every frame and carries the pole itself,
        /// so a world-space tilt written here would only be a stale snapshot.
        /// </summary>
        private static void ApplyBodyTilt(string key, RotateObject rot, Vector3 target)
        {
            if (OwnedByFaceAttractorFake(rot))
            {
                return;
            }

            target = LowerNorthPole.Contains(key) ? -target : target;
            target.Normalize();

            Vector3 current = GetSpinAxis(rot);
            if (current.sqrMagnitude < 1e-8f)
            {
                return;
            }

            rot.transform.rotation = Quaternion.FromToRotation(current, target) * rot.transform.rotation;
        }

        internal static Vector3 GetSpinAxis(RotateObject rot)
        {
            return (rot.transform.rotation * rot.directionRotation).normalized;
        }

        /// <summary>
        /// True for a view whose orientation the game rebuilds every frame (a fake object with
        /// <c>faceAttractor</c>, i.e. the Moon). A world-space tilt written once would be carried
        /// around by that update, so the fake frame owns the pole instead.
        /// </summary>
        private static bool OwnedByFaceAttractorFake(RotateObject rot)
        {
            for (Transform t = rot.transform.parent; t != null; t = t.parent)
            {
                FakeObjectRotateAsRealObject fake = t.GetComponent<FakeObjectRotateAsRealObject>();
                if (fake != null)
                {
                    return fake.faceAttractor;
                }
            }

            return false;
        }

        /// <summary>
        /// Observed magnetic axis for a body key, when the magnetic-axis offset is enabled. With
        /// <see cref="TiltMagneticAxes"/> off, no body has one and the spin axis is used instead.
        /// </summary>
        private static bool TryGetMagneticAxis(string key, out MagneticAxis axis)
        {
            if (TiltMagneticAxes && key != null && MagneticAxes.TryGetValue(key, out axis))
            {
                return true;
            }

            axis = default;
            return false;
        }

        /// <summary>
        /// Shared body-fixed rotation for the aurora and magnetosphere axis. Earth's calibrated
        /// frame has longitude zero on +X and east toward +Z. Rx tilts +Y toward +Z, while positive
        /// Ry turns +Z toward +X, so Unity yaw is 90 degrees minus east longitude.
        /// Without a calibrated longitude, use only the tilt; its surface direction is arbitrary.
        /// </summary>
        private static Quaternion LocalMagneticRotation(MagneticAxis axis)
        {
            Quaternion tiltRotation = Quaternion.AngleAxis((float)axis.Tilt, Vector3.right);
            return axis.LongitudeEast.HasValue
                ? Quaternion.AngleAxis((float)(90.0 - axis.LongitudeEast.Value), Vector3.up) * tiltRotation
                : tiltRotation;
        }

        /// <summary>Magnetic axis for a body key, in the body frame; the spin axis when none is known.</summary>
        internal static Vector3 LocalMagneticAxis(string key)
        {
            TryGetMagneticAxis(key, out MagneticAxis axis);
            return (LocalMagneticRotation(axis) * Vector3.up).normalized;
        }

        /// <summary>
        /// Aligns the aurora with the body's magnetic axis. As a child of the rotating View,
        /// its fixed local tilt makes the caps move around the spin axis with the planet.
        /// Bodies without a magnetic-axis tilt keep their authored rotation.
        /// </summary>
        private static void AlignAurora(Transform view, string key)
        {
            if (view == null || !TryGetMagneticAxis(key, out MagneticAxis axis) || axis.Tilt == 0.0)
            {
                return;
            }

            Quaternion local = LocalMagneticRotation(axis) * AuroraPhase;
            for (int i = 0; i < view.childCount; i++)
            {
                Transform child = view.GetChild(i);
                if (child.name.StartsWith("AuroraBorealis", StringComparison.Ordinal))
                {
                    child.localRotation = local;
                }
            }
        }


        /// <summary>
        /// Ring meshes are siblings of the planet's View (Saturn), not its children, so
        /// they have to be tilted separately. Aligns each ring's local Z (the torus normal)
        /// with the planet's tilted equator.
        /// </summary>
        private static void AlignRings(ObjectInfo body, Vector3 target)
        {
            if (body == null)
            {
                return;
            }

            NBody nbody = body.NBody;
            if (nbody == null)
            {
                return;
            }

            target.Normalize();
            Transform root = nbody.transform;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (!child.name.StartsWith("Ring", StringComparison.Ordinal))
                {
                    continue;
                }

                Vector3 normal = child.rotation * Vector3.forward;
                child.rotation = Quaternion.FromToRotation(normal, target) * child.rotation;
            }
        }

        internal static string ResolveBodyKey(RotateObject rot, out ObjectInfo body)
        {
            body = null;

            ObjectInfo info = rot.GetComponentInParent<ObjectInfo>();
            bool hasObjectInfo = info != null;
            for (int depth = 0; depth < 8 && info != null; depth++)
            {
                if (IsBody(info))
                {
                    body = info;
                    return KeyOf(info);
                }

                // Climb only through orbit pseudo-objects. Anything else (a pulled asteroid, a
                // station, a spacecraft) is its own object and must not inherit the planet's tilt.
                if (info.objectTypes != EObjectTypes.Orbit)
                {
                    return null;
                }

                info = info.ParentObjectInfo;
            }

            // No ObjectInfo at all: low-orbit visual duplicates ("LowOrbitEarthFake",
            // "MoonFake", ...) are named after the body they depict, a reliable last resort.
            return hasObjectInfo ? null : KeyFromTransform(rot.transform);
        }

        internal static bool TryGetAxisTarget(string key, ObjectInfo body, out Vector3 target)
        {
            if (PlanetTilts.TryGetValue(key, out double planetTilt))
            {
                return TryGetPlanetPole(key, planetTilt, body, out target);
            }

            if (key == "Moon")
            {
                target = Vector3.forward;

                // Cassini state 2: the spin axis sits 6.687 deg from the orbit normal, inside the
                // plane of that normal and the ecliptic normal. Its azimuth is set by the node,
                // not free. The resulting ecliptic tilt follows the selected frozen inclination;
                // the familiar ~1.54 deg value corresponds to a mean orbit inclination of ~5.15 deg.
                if (!TryGetMoonOrbitElements(MoonOrbits["Moon"], out _, out _, out Vector3 normal))
                {
                    // The frame offset is not calibrated yet; the caller leaves the body alone.
                    return false;
                }

                Vector3 ecliptic = Vector3.forward;
                Vector3 inPlane = ecliptic - normal * Vector3.Dot(ecliptic, normal);
                if (inPlane.sqrMagnitude < 1e-8f)
                {
                    return false;
                }

                inPlane.Normalize();
                double angle = MoonSpinAxisToOrbitNormal * DegToRad;
                target = (normal * (float)Math.Cos(angle) + inPlane * (float)Math.Sin(angle)).normalized;
                return true;
            }

            if (MoonOrbits.TryGetValue(key, out MoonOrbit moon))
            {
                return TryGetMoonOrbitElements(moon, out _, out _, out target);
            }

            if (body != null && body.objectTypes == EObjectTypes.Moons)
            {
                OrbitUniversal orbit = body.OrbitUniversal;
                if (orbit != null)
                {
                    target = GameOrbitNormal(orbit.inclination, orbit.omega_uc);
                    return true;
                }
            }

            target = Vector3.forward;
            return false;
        }

        /// <summary>
        /// Real pole of a planet. Earth uses the solstice direction; the others use the published
        /// IAU pole shifted by the game frame's own rotation about the ecliptic normal. When that
        /// data is not available yet (scene not ready) the caller leaves the body untouched.
        /// </summary>
        internal static bool TryGetPlanetPole(string name, double obliquity, ObjectInfo body, out Vector3 pole)
        {
            pole = Vector3.forward;

            if (name == "Earth")
            {
                // Earth's solstice pole needs an ObjectInfo, but callers such as the parked-ship
                // rings and fake views have none. Resolve Earth here, or accept the pole cached by
                // an earlier calibration of this scene.
                if (body == null && !earthPoleResolved && !TryGetEarthBody(out body))
                {
                    return false;
                }

                return TryGetSolsticePole(body, obliquity, out pole);
            }

            if (!PlanetAxes.TryGetValue(name, out (double Ra, double Dec) axis)
                || !TryGetFrameOffsetCached(out double frameOffset))
            {
                return false;
            }

            EclipticCoordinates(axis.Ra, axis.Dec, out double longitude, out double latitude);
            double lambda = (longitude + frameOffset) * DegToRad;
            double beta = latitude * DegToRad;

            double cosBeta = Math.Cos(beta);
            pole = new Vector3(
                (float)(cosBeta * Math.Cos(lambda)),
                (float)(cosBeta * Math.Sin(lambda)),
                (float)Math.Sin(beta)).normalized;
            return true;
        }

        /// <summary>Signed rotation of the game's ecliptic frame against the real one, from Earth.</summary>
        private static bool TryGetFrameOffset(ObjectInfo earth, out double offset)
        {
            offset = 0.0;
            if (earth == null || !TryGetSolsticePole(earth, PlanetTilts["Earth"], out Vector3 gamePole))
            {
                return false;
            }

            double gameLongitude = Math.Atan2(gamePole.y, gamePole.x) * RadToDeg;
            offset = gameLongitude - RealEarthPoleLongitude;
            return true;
        }

        internal static bool frameOffsetKnown;
        private static double frameOffsetValue;

        /// <summary>Frame offset, computed once per scene from Earth's own orbit.</summary>
        internal static bool TryGetFrameOffsetCached(out double offset)
        {
            if (frameOffsetKnown)
            {
                offset = frameOffsetValue;
                return true;
            }

            ObjectInfo earth = null;
            ObjectInfo[] infos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            for (int i = 0; i < infos.Length; i++)
            {
                ObjectInfo info = infos[i];
                if (info != null && info.objectTypes == EObjectTypes.Planet && KeyOf(info) == "Earth")
                {
                    earth = info;
                    break;
                }
            }

            if (!TryGetFrameOffset(earth, out frameOffsetValue))
            {
                offset = 0.0;
                return false;
            }

            frameOffsetKnown = true;
            offset = frameOffsetValue;
            return true;
        }

        /// <summary>Ecliptic longitude and latitude of a pole given its equatorial coordinates.</summary>
        private static void EclipticCoordinates(double rightAscension, double declination, out double longitude, out double latitude)
        {
            const double Obliquity = 23.4393 * DegToRad;
            double a = rightAscension * DegToRad;
            double d = declination * DegToRad;

            double x = Math.Cos(d) * Math.Cos(a);
            double y = Math.Cos(d) * Math.Sin(a) * Math.Cos(Obliquity) + Math.Sin(d) * Math.Sin(Obliquity);
            double z = -Math.Cos(d) * Math.Sin(a) * Math.Sin(Obliquity) + Math.Sin(d) * Math.Cos(Obliquity);

            longitude = Math.Atan2(y, x) * RadToDeg;
            latitude = Math.Asin(Math.Max(-1.0, Math.Min(1.0, z))) * RadToDeg;
        }

        private static ObjectInfo earthBody;
        internal static bool earthPoleResolved;
        internal static bool scenePlanesApplied;
        private static Vector3 earthPoleVector;

        internal static void InvalidateSceneOrientation()
        {
            earthBody = null;
            earthPoleResolved = false;
            frameOffsetKnown = false;
            scenePlanesApplied = false;
        }

        /// <summary>Earth's ObjectInfo, scanned once per scene.</summary>
        private static bool TryGetEarthBody(out ObjectInfo earth)
        {
            earth = earthBody;
            if (earth != null)
            {
                return true;
            }

            ObjectInfo[] infos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            for (int i = 0; i < infos.Length; i++)
            {
                ObjectInfo info = infos[i];
                if (info != null && info.objectTypes == EObjectTypes.Planet && KeyOf(info) == "Earth")
                {
                    earthBody = info;
                    earth = info;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Apply the dependent planes once the game is ready. Resolving Earth's pole is only
        /// a prerequisite: TiltView can cache it before this pass has rebuilt any moon orbit.
        /// Called by readiness events (GravityEngine.Setup and TimeController initialization),
        /// scene/save load and hot reload; there is no per-frame polling or frame delay.
        /// </summary>
        internal static bool TryResolveEarthPole()
        {
            if (scenePlanesApplied)
            {
                return true;
            }

            // Do not rebuild state vectors while GravityEngine is still initializing them.
            // The completion of Setup invokes this again if an earlier event arrived too soon.
            if (MonoBehaviourSingleton<TimeController>.Instance == null
                || GravityEngine.instance == null || !GravityEngine.instance.IsSetup())
            {
                return false;
            }

            if (!TryGetEarthBody(out ObjectInfo earth)
                || !TryGetSolsticePole(earth, PlanetTilts["Earth"], out _))
            {
                return false;
            }

            frameOffsetKnown = false;
            ReapplyPlanetTilts();
            ReapplyMoonPlanes();
            ReapplyLowOrbitPlanes();
            // Init() rebuilds OrbitUniversal and NBody's initial vectors, but not the active
            // GravityState. Refresh its fixed bodies at the SAME time (also updates paused views
            // and children in orbit-depth order), so they immediately follow the new planes.
            GravityEngine.instance.SetPhysicalTime(GravityEngine.instance.GetPhysicalTimeDouble(), force: true);
            AlignOrbitRings();
            scenePlanesApplied = true;
            InitializeFields();
            return true;
        }

        private static void ReapplyPlanetTilts()
        {
            RotateObject[] rotations = Resources.FindObjectsOfTypeAll<RotateObject>();
            for (int i = 0; i < rotations.Length; i++)
            {
                RotateObject rot = rotations[i];
                if (rot == null)
                {
                    continue;
                }

                try
                {
                    string key = ResolveBodyKey(rot, out ObjectInfo body);
                    if (key == null || !TryGetAxisTarget(key, body, out Vector3 target))
                    {
                        continue;
                    }

                    ApplyBodyTilt(key, rot, target);
                    AlignAurora(rot.transform, key);
                    AlignRings(body, target);
                }
                catch (Exception error)
                {
                    ReportError($"Could not re-apply the tilt of '{(rot == null ? "<null>" : rot.name)}'", error);
                }
            }
        }

        /// <summary>
        /// Calibrates the pole against Earth's authored orbit on 21 June of the current game year.
        /// The cached orientation drifts against the calendar over time; loading a save recalibrates it.
        /// </summary>
        private static bool TryGetSolsticePole(ObjectInfo body, double obliquity, out Vector3 pole)
        {
            pole = Vector3.zero;

            if (earthPoleResolved)
            {
                pole = earthPoleVector;
                return true;
            }

            OrbitUniversal orbit = body.OrbitUniversal;
            TimeController time = MonoBehaviourSingleton<TimeController>.Instance;
            if (orbit == null || time == null)
            {
                return false;
            }

            DateTime solstice = new DateTime(time.CurrentTime.Year, 6, 21);
            double physicsTime = GravityScaler.WorldSecsToPhysTime((solstice - time.StartDateTime).TotalSeconds);

            OrbitPropagator propagator = OrbitPropagator.GetPropagator(orbit);
            Vector3d position = propagator.PropagateToTime(physicsTime).Item1;
            if (double.IsNaN(position.x) || double.IsNaN(position.y) || double.IsNaN(position.z))
            {
                return false;
            }

            // Sun as seen from Earth at the solstice; the azimuth lives in the ecliptic plane.
            // The sign is the one that puts the north pole toward the Sun in June in this scene's
            // frame (the opposite convention makes the seasons come out reversed).
            Vector3 direction = position.ToVector3();
            direction.z = 0f;
            if (direction.sqrMagnitude < 1e-10f)
            {
                return false;
            }
            direction.Normalize();

            double tilt = obliquity * DegToRad;
            pole = ((float)Math.Cos(tilt) * Vector3.forward + (float)Math.Sin(tilt) * direction).normalized;

            earthPoleVector = pole;
            earthPoleResolved = true;
            return true;
        }

        /// <summary>
        /// Resolves a body's target pole from its hierarchy: the real planet spin pole or
        /// the moon's JPL orbital pole, converted from its published reference plane.
        /// </summary>
        internal static bool TryGetPoleForTransform(Transform transform, out Vector3 pole)
        {
            pole = Vector3.forward;
            string key = KeyFromTransform(transform);
            return key != null && TryGetAxisTarget(key, null, out pole);
        }

        internal static bool IsBody(ObjectInfo info)
        {
            switch (info.objectTypes)
            {
                case EObjectTypes.Planet:
                case EObjectTypes.DwarfPlanet:
                case EObjectTypes.Moons:
                case EObjectTypes.Protoplanet:
                    return true;
                default:
                    return false;
            }
        }

        internal static string KeyOf(ObjectInfo info)
        {
            string id = info.idTranslation;
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            int dot = id.LastIndexOf('.');
            return dot >= 0 ? id.Substring(dot + 1) : id;
        }

        internal static string KeyFromTransform(Transform transform)
        {
            for (int depth = 0; transform != null && depth < 12; depth++, transform = transform.parent)
            {
                string name = transform.name;
                if (name == "View" || name == "View (1)" || name == "PlanetModel" || name == "DwarfPlanet" || name == "SunModelView")
                {
                    continue;
                }

                name = name.Replace("LowOrbit", string.Empty).Replace("Fake", string.Empty);

                int start = 0;
                while (start < name.Length && (char.IsDigit(name[start]) || name[start] == ' '))
                {
                    start++;
                }
                name = name.Substring(start);

                if (KnownBodies.Contains(name))
                {
                    return name;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The clock provides the start date. If physics is not ready yet, the Setup postfix below
    /// completes the pass; if physics was initialized first, this event supplies the final prerequisite.
    /// </summary>
    [HarmonyPatch(typeof(TimeController), "InitializeSingleton")]
    internal static class TimeControllerReadyPatch
    {
        private static void Postfix()
        {
            try
            {
                BodyOrientation.TryResolveEarthPole();
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not resolve Earth's pole", error);
            }
        }
    }

    /// <summary>
    /// Setup finishes body initialization, PreEvolve, GEStart callbacks and center-of-mass
    /// adjustment. Its postfix is the exact physics-ready point and is removed on hot unload.
    /// A new Setup rebuilds the authored orbits, so invalidate the previous completed pass.
    /// </summary>
    [HarmonyPatch(typeof(GravityEngine), nameof(GravityEngine.Setup))]
    internal static class GravityEngineReadyPatch
    {
        private static void Postfix()
        {
            try
            {
                BodyOrientation.InvalidateSceneOrientation();
                BodyOrientation.TryResolveEarthPole();
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not apply orbital planes after GravityEngine setup", error);
            }
        }
    }

}
