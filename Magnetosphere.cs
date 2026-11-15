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
using static TiltedWorlds.OrbitPlanes;
using static TiltedWorlds.MagnetosphereGeometry;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    internal static class Magnetosphere
    {
#if TILTEDWORLDS_GPU
        internal enum MagnetosphereRendererMode { Auto, CPU }
        internal static BepInEx.Configuration.ConfigEntry<MagnetosphereRendererMode> MagnetosphereRenderer;
#endif

        // Only bypass our GenerateFieldLines prefix while capturing an untouched vanilla template.
        internal static bool GeneratingVanillaTemplate;
        internal static readonly System.Reflection.FieldInfo VanillaLinesField =
            AccessTools.Field(typeof(MagneticField), "fieldLines")
            ?? throw new MissingFieldException(typeof(MagneticField).FullName, "fieldLines");

        internal static bool DynamicMagnetospheres = true;
        internal static bool CustomFieldGeometry => DynamicMagnetospheres;
        internal sealed class FieldState
        {
            public bool GeometryInitialized;
#if TILTEDWORLDS_GPU
            public GpuMagnetosphere Gpu;
#endif
            public MagneticField Field;
            public List<LineRenderer> Lines;
            public List<Vector3[]> Target;
            public List<Vector3[]> Current;
            public List<float[]> TailWeights;
            public float BoundsRadius;
            public float NextGeometryTime;
            public bool GeometryVisible;
            public Quaternion DisplayRotation;
            public float ScreenDiameter = float.PositiveInfinity;
            public float LiveGeometryBlend;
            public bool HasGeometryBlend;
            public bool HasFixedGeometryMode;
            public bool UseStaticTemplate;
            public bool StaticTemplateUploaded;
            public bool GeometryBlendChanged;
            public Quaternion LastTailRotation;
            public Vector3 LastWind;
            public bool HasTailRotation;
            public Quaternion LastFrameRotation;
            public bool HasFrameRotation;
            public bool PoolReady;
        }

        internal static int cameraFrame = -1;
        internal static Camera geometryCamera;
        internal static readonly Plane[] geometryPlanes = new Plane[6];

        internal static readonly ConditionalWeakTable<Transform, FieldState> FieldStates =
            new ConditionalWeakTable<Transform, FieldState>();

        /// <summary>Called by scene/physics readiness and field-creation events, never by Update.</summary>
        internal static void InitializeFields()
        {
            foreach (MagneticField magnetic in Resources.FindObjectsOfTypeAll<MagneticField>())
            {
                if (magnetic == null || !magnetic.gameObject.scene.IsValid()
                    || !magnetic.gameObject.scene.isLoaded) continue;
                try
                {
                    FieldState state = PrepareField(magnetic);
                    // Zero lines is a completed initialization too. A later vanilla
                    // GenerateFieldLines call handles changes to the requested line count.
                    if (!state.GeometryInitialized) magnetic.GenerateFieldLines();
                }
                catch (Exception error)
                {
                    ReportError($"Could not initialize magnetic field '{magnetic.name}'", error);
                }
            }
        }

        internal static FieldState PrepareField(MagneticField magnetic)
        {
            FieldState state = FieldStates.GetOrCreateValue(magnetic.transform);
            state.Field = magnetic;
            // The completed orbital pass supplies the final view and orbit normals.
            // Fields generated earlier keep the dynamic mode until that event arrives.
            if (CustomFieldGeometry && scenePlanesApplied && !state.HasFixedGeometryMode)
            {
                Transform view = magnetic.transform.parent;
                RotateObject rot = view != null ? view.GetComponent<RotateObject>() : null;
                if (rot != null)
                {
                    string key = ResolveBodyKey(rot, out ObjectInfo body);
                    if (key != null) SelectStaticGeometry(key, body, rot);
                }
#if TILTEDWORLDS_GPU
                if (state.UseStaticTemplate && state.Gpu != null)
                {
                    // Generation can precede physics readiness. Apply a late static decision
                    // to an already-created GPU field without regenerating its template.
                    state.Gpu.Release();
                    Destroy(state.Gpu.gameObject);
                    state.Gpu = null;
                }
#endif
            }
            return state;
        }

        internal static void SetGeometryCamera(Camera camera)
        {
            geometryCamera = camera;
            cameraFrame = -1;
        }

        // One-shot discovery covers existing cameras on hot reload and scene transitions.
        internal static void RefreshGeometryCamera() => SetGeometryCamera(Camera.main);
        // Select a permanent per-field mode from the all-phase bound once valid data are available.
        // All angles refer to the unoriented magnetic axis, so retrograde poles are equivalent.
        internal static bool SelectStaticGeometry(string key, ObjectInfo body, RotateObject rot)
        {
            if (body == null || rot == null) return false;
            // Cloud layers and low-orbit duplicates can have a different local rotation axis.
            // Use exactly the transform used by OrientMagneticField, not every RotateObject.
            Transform magneticView = body.magneticField != null ? body.magneticField.transform.parent
                : body.view != null ? body.view.transform : null;
            if (magneticView == null || rot.transform != magneticView) return false;
            // During save loading the view may still have its authored, untilted orientation.
            // Do not permanently select the rigid template until the real pole is applied.
            if (!TryGetAxisTarget(key, body, out Vector3 expectedAxis)) return false;
            Vector3 currentAxis = GetSpinAxis(rot);
            if (currentAxis.sqrMagnitude < 0.5f || expectedAxis.sqrMagnitude < 0.5f
                || Mathf.Abs(Vector3.Dot(currentAxis.normalized, expectedAxis.normalized)) < 0.99999f)
                return false;
            OrbitUniversal orbit = body.OrbitUniversal;
            double offsetMax = 0.0;
            OrbitUniversal solarOrbit = null;
            for (int depth = 0; depth < 8; depth++)
            {
                if (orbit == null || orbit.centerNbody == null)
                { break; }
                if (double.IsNaN(orbit.p) || double.IsInfinity(orbit.p) || orbit.p <= 0
                    || double.IsNaN(orbit.eccentricity) || orbit.eccentricity < 0 || orbit.eccentricity >= 1)
                { break; }
                ObjectInfo centre = orbit.centerNbody.GetObjectInfo();
                string centreKey = centre == null ? null : KeyOf(centre);
                if (centreKey == "Sun" || orbit.centerNbody.name == "Sun")
                { solarOrbit = orbit; break; }
                if (centre == null || !IsBody(centre))
                { break; }
                // p is the live semilatus rectum; all levels use GravityEngine length units.
                offsetMax += orbit.p / (1.0 - orbit.eccentricity);
                orbit = centre.OrbitUniversal;
            }
            if (solarOrbit == null) return false;

            Vector3 normal = GameOrbitNormal(solarOrbit.inclination, solarOrbit.omega_uc).normalized;
            Vector3 liveNormal = solarOrbit.GetAxis().ToVector3();
            Vector3 spin = GetSpinAxis(rot);
            Vector3 localSpin = rot.directionRotation.normalized;
            Vector3 localMagnetic = LocalMagneticAxis(key);
            double alpha = Math.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(spin, normal)))) * RadToDeg;
            double beta = Math.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(localSpin, localMagnetic)))) * RadToDeg;
            double perihelion = solarOrbit.p / (1.0 + solarOrbit.eccentricity);
            double ratio = offsetMax / perihelion;
            double margin = Math.Asin(Math.Min(1.0, ratio)) * RadToDeg;
            double bound = Math.Min(90.0, alpha + beta + margin);
            double normalError = liveNormal.sqrMagnitude > 0.5f
                ? Math.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(liveNormal.normalized, normal)))) * RadToDeg
                : double.NaN;
            bool valid = spin.sqrMagnitude > 0.5f && localSpin.sqrMagnitude > 0.5f
                && !double.IsNaN(bound) && !double.IsNaN(normalError) && normalError < 0.1 && ratio < 1.0;
            if (valid && body.magneticField != null)
            {
                FieldState state = FieldStates.GetOrCreateValue(body.magneticField.transform);
                if (!state.HasFixedGeometryMode)
                {
                    // The Moon is deliberately kept exact: its small tilt is visible early in play.
                    // Include the measured orbit-normal discrepancy and a small rounding margin.
                    state.UseStaticTemplate = key != "Moon" && bound + normalError + 0.01 < 10.0;
                    state.HasFixedGeometryMode = true;
                }
            }
            return valid;
        }
        internal static void PrepareRigidField(MagneticField magnetic)
        {
            PrepareField(magnetic).GeometryInitialized = false;
            // Hide deferred destruction immediately and discard any geometry left by hot reload.
            foreach (LineRenderer line in magnetic.GetComponentsInChildren<LineRenderer>(true))
            {
                line.enabled = false;
                Destroy(line.gameObject);
            }
            ((List<LineRenderer>)VanillaLinesField.GetValue(magnetic)).Clear();
        }

        internal static void FinishRigidField(MagneticField magnetic)
        {
            foreach (LineRenderer line in (List<LineRenderer>)VanillaLinesField.GetValue(magnetic))
                line.useWorldSpace = false;
            FieldStates.GetOrCreateValue(magnetic.transform).GeometryInitialized = true;
        }

        /// <summary>
        /// Replaces the game's LookAt for a body's magnetosphere, so the field follows the body's real
        /// magnetic axis instead of the ecliptic normal.
        ///
        /// <see cref="CustomFieldGeometry"/> picks between two ways of keeping the tail pointed away
        /// from the Sun:
        ///  - custom geometry: the plugin owns the lines. The transform holds the exact magnetic axis
        ///    (roll anchored to the Sun so the seed pattern does not swing with the spin) and every frame
        ///    the template is deformed for the real wind direction on the CPU or GPU.
        ///  - rigid mode: the game's lines are rotated, with the axis projected onto the plane facing the
        ///    Sun; cheap, but the visible axis trails the real one by up to the dipole tilt.
        ///
        /// Either way the magnetic axis is read from the live View rotation, so it cones around the spin
        /// axis as the body turns. Nothing runs while the magnetospheres are switched off: the game
        /// deactivates the whole object and Unity stops calling this Update.
        ///
        /// Returns true when the object was handled and the game's own LookAt must be skipped.
        /// </summary>
        internal static bool OrientMagneticField(LookAt lookAt)
        {
            Transform field = lookAt.transform;
            if (!field.name.StartsWith("MagneticField", StringComparison.Ordinal))
            {
                // Comet tails and every other LookAt user keep the authored behaviour.
                return false;
            }

            Transform view = field.parent;
            string key = view != null ? KeyFromTransform(field) : null;
            if (key == null)
            {
                return false;
            }

            Transform sun = lookAt.Target;
            if (sun == null)
            {
                // The game's own Update would throw on a null target; leave the field where it is.
                return true;
            }

            Vector3 toSun = sun.position - field.position;
            if (toSun.sqrMagnitude < 1e-10f)
            {
                return true;
            }
            toSun.Normalize();

            // The real magnetic axis, in world space.
            Vector3 realAxis = (view.rotation * LocalMagneticAxis(key)).normalized;

            // Lifecycle hooks own component discovery and template creation. Until one
            // has registered this field, keep the vanilla orientation behaviour.
            if (!FieldStates.TryGetValue(field, out FieldState state)) return false;

            if (CustomFieldGeometry)
            {
                // Exact axis. The minimal rotation leaves the roll (and with it the azimuth of the traced
                // seeds) following the body's spin, so twist about the axis until the seeds face the Sun;
                // the axis stays exact and the wind is handled by the geometry itself.
                Quaternion rotation = state.HasFrameRotation
                    ? Quaternion.FromToRotation(state.LastFrameRotation * Vector3.up, realAxis)
                        * state.LastFrameRotation
                    : Quaternion.FromToRotation(Vector3.up, realAxis);

                Vector3 sunward = toSun - realAxis * Vector3.Dot(toSun, realAxis);
                if (sunward.sqrMagnitude > 1e-8f)
                {
                    float projectionLength = sunward.magnitude;
                    sunward /= projectionLength;
                    Vector3 seedForward = rotation * Vector3.forward;
                    // This is an oriented direction, not an unoriented axis: +Z must face
                    // the Sun. Choosing the opposite sign locks the template facing backwards
                    // and forces a nearly 180-degree deformation even for an untilted dipole.
                    // Near parallel alignment retain transported roll only inside this cone.
                    float rollBlend = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(0.02f, 0.15f, projectionLength));
                    rotation = Quaternion.AngleAxis(
                        Vector3.SignedAngle(seedForward, sunward, realAxis) * rollBlend, realAxis) * rotation;
                }
                state.LastFrameRotation = rotation.normalized;
                state.HasFrameRotation = true;
                UpdateFieldGeometry(field, state, toSun, key, rotation);
                return true;
            }

            // Rigid mode. Step 1: turn the real axis inside the (axis, Sun) plane until it is
            // perpendicular to the Sun direction.
            Vector3 axis = realAxis - toSun * Vector3.Dot(realAxis, toSun);
            if (axis.sqrMagnitude < 1e-8f)
            {
                // The magnetic axis points straight at (or away from) the Sun, so that plane is
                // undefined. Fall back to the spin axis, then to any perpendicular.
                Vector3 spin = view.up;
                axis = spin - toSun * Vector3.Dot(spin, toSun);
                if (axis.sqrMagnitude < 1e-8f)
                {
                    axis = Vector3.Cross(toSun, Mathf.Abs(toSun.y) < 0.9f ? Vector3.up : Vector3.right);
                }
            }
            axis.Normalize();

            // Step 2: yaw about that axis so the sunward end (local +Z) lands on the Sun. The axis is
            // already perpendicular to the Sun direction, so this lands exactly on it.
            Quaternion aligned = Quaternion.FromToRotation(Vector3.up, axis);
            Vector3 nose = aligned * Vector3.forward;
            field.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(nose, toSun, axis), axis) * aligned;
            return true;
        }
    }

    [HarmonyPatch(typeof(ObjectInfoManager), "AddMagneticFieldScript")]
    internal static class MagneticFieldCreatedPatch
    {
        private static void Postfix() => Magnetosphere.InitializeFields();
    }

    [HarmonyPatch(typeof(CameraControl.MyCameraController), "Awake")]
    internal static class MagnetosphereCameraReadyPatch
    {
        private static void Postfix(CameraControl.MyCameraController __instance)
            => Magnetosphere.SetGeometryCamera(__instance.MyCamera);
    }

    [HarmonyPatch(typeof(CameraControl.MyCameraController), "OnDestroy")]
    internal static class MagnetosphereCameraDestroyedPatch
    {
        private static void Prefix(CameraControl.MyCameraController __instance)
        {
            if (Magnetosphere.geometryCamera == __instance.MyCamera)
                Magnetosphere.SetGeometryCamera(null);
        }
    }

    /// <summary>
    /// The game rebuilds the field lines when the body's habitability changes the line count. In custom
    /// geometry mode the plugin takes that rebuild over: it builds its own pool of renderers and from
    /// then on only moves their vertices, so nothing is destroyed while the field is alive and the
    /// shader animation never restarts.
    /// </summary>
    [HarmonyPatch(typeof(MagneticField), nameof(MagneticField.GenerateFieldLines))]
    internal static class MagneticFieldRebuildPatch
    {
        private static bool Prefix(MagneticField __instance)
        {
            if (!Magnetosphere.CustomFieldGeometry)
            {
                Magnetosphere.PrepareRigidField(__instance);
                return true;
            }
            if (Magnetosphere.GeneratingVanillaTemplate)
            {
                return true;
            }

            try
            {
                MagnetosphereGeometry.RebuildFieldPool(__instance);
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not rebuild a magnetosphere", error);
            }

            return false;
        }

        private static void Postfix(MagneticField __instance)
        {
            if (!Magnetosphere.CustomFieldGeometry) Magnetosphere.FinishRigidField(__instance);
        }
    }

    /// <summary>
    /// The game turns a magnetosphere with a LookAt aimed at the Sun that also keeps the Sun's own up
    /// vector, which pinned the dipole to the ecliptic normal. Take those over so the field instead
    /// follows the body's real magnetic axis and still faces the Sun. Every other LookAt (comet
    /// tails and friends) is left untouched.
    /// </summary>
    [HarmonyPatch(typeof(LookAt), "Update")]
    internal static class MagneticFieldOrientationPatch
    {
        private static bool Prefix(LookAt __instance)
        {
            try
            {
                return !Magnetosphere.OrientMagneticField(__instance);
            }
            catch (Exception error)
            {
                Plugin.ReportError("Could not orient a magnetic field", error);
                return true;
            }
        }
    }

}
