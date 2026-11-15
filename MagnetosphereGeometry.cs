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
using static TiltedWorlds.Magnetosphere;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    internal static class MagnetosphereGeometry
    {
        internal static void RebuildFieldPool(MagneticField magnetic)
        {
            if (magnetic != null) RebuildVanillaTemplate(magnetic);
        }

        // Local mesh radius is 0.5: start bending at two planetary radii and finish at twelve.
        private const float DeformStartRadius = 1f;
        private const float DeformEndRadius = 6f;

        private static void RebuildVanillaTemplate(MagneticField magnetic)
        {
            Transform field = magnetic.transform;
            FieldState state = PrepareField(magnetic);
            state.GeometryInitialized = false;
            state.PoolReady = false;
            state.HasTailRotation = false;
            state.StaticTemplateUploaded = false;
#if TILTEDWORLDS_GPU
            if (state.Gpu != null)
            {
                state.Gpu.Release();
                Destroy(state.Gpu.gameObject);
                state.Gpu = null;
            }
#endif
            foreach (LineRenderer oldLine in field.GetComponentsInChildren<LineRenderer>(true))
            {
                // Destroy is deferred: hide old geometry immediately during hot reload.
                oldLine.enabled = false;
                Destroy(oldLine.gameObject);
            }

            var vanillaLines = (List<LineRenderer>)VanillaLinesField.GetValue(magnetic);
            vanillaLines.Clear();
            GeneratingVanillaTemplate = true;
            try
            {
                magnetic.GenerateFieldLines();
            }
            finally
            {
                GeneratingVanillaTemplate = false;
            }

            state.Field = magnetic;
            state.Lines = new List<LineRenderer>(vanillaLines);
            state.Target = new List<Vector3[]>();
            state.Current = new List<Vector3[]>();
            state.TailWeights = new List<float[]>();
            state.BoundsRadius = 0f;
            state.GeometryVisible = false;
            foreach (LineRenderer line in state.Lines)
            {
                // Vanilla writes local coordinates regardless of the prefab's space flag.
                var rest = new Vector3[line.positionCount];
                line.GetPositions(rest);
                line.useWorldSpace = false;
                SeparateVanillaTailBranches(rest);
                foreach (Vector3 point in rest)
                    state.BoundsRadius = Mathf.Max(state.BoundsRadius, point.magnitude);
                rest = SimplifyFieldLine(rest);
                line.positionCount = rest.Length;
                line.SetPositions(rest);
                var weights = new float[rest.Length];
                for (int p = 0; p < rest.Length; p++)
                {
                    float t = Mathf.Clamp01((rest[p].magnitude - DeformStartRadius)
                                            / (DeformEndRadius - DeformStartRadius));
                    weights[p] = t * t * t * (t * (t * 6f - 15f) + 10f);
                }
                state.Target.Add(rest);
                state.Current.Add(new Vector3[rest.Length]);
                state.TailWeights.Add(weights);
            }
            state.PoolReady = state.Lines.Count > 0;
#if TILTEDWORLDS_GPU
            if (state.PoolReady && !state.UseStaticTemplate
                && MagnetosphereRenderer.Value == MagnetosphereRendererMode.Auto)
                state.Gpu = GpuMagnetosphere.TryCreate(field, state.Lines, state.Target, state.TailWeights, state.BoundsRadius);
#endif
            state.GeometryInitialized = true;
        }

        // Ramer-Douglas-Peucker with an extra segment-length constraint in the bending zone.
        // The latter keeps straight rest segments sampled when deformation curves them later.
        internal static Vector3[] SimplifyFieldLine(Vector3[] points)
        {
            if (points.Length < 3) return points;
            var keep = new bool[points.Length];
            keep[0] = keep[points.Length - 1] = true;
            var ranges = new Stack<int>();
            ranges.Push(0);
            ranges.Push(points.Length - 1);
            while (ranges.Count > 0)
            {
                int end = ranges.Pop();
                int start = ranges.Pop();
                if (end - start < 2) continue;
                Vector3 delta = points[end] - points[start];
                float squared = delta.sqrMagnitude;
                float worst = 0f;
                int split = -1;
                bool bending = false;
                for (int i = start + 1; i < end; i++)
                {
                    float radius = points[i].magnitude;
                    bending |= radius > DeformStartRadius && radius < DeformEndRadius;
                    float t = squared > 1e-12f
                        ? Mathf.Clamp01(Vector3.Dot(points[i] - points[start], delta) / squared) : 0f;
                    float error = (points[i] - (points[start] + delta * t)).sqrMagnitude;
                    // Tight near-surface tolerance; allow a little more in the long tail.
                    float tolerance = radius < DeformEndRadius ? 0.003f : 0.015f;
                    float score = error / (tolerance * tolerance);
                    if (score > worst) { worst = score; split = i; }
                }
                if (worst <= 1f)
                {
                    if (!bending || squared <= 0.15f * 0.15f) continue;
                    split = (start + end) / 2;
                }
                keep[split] = true;
                ranges.Push(start); ranges.Push(split);
                ranges.Push(split); ranges.Push(end);
            }
            var result = new List<Vector3>();
            for (int i = 0; i < points.Length; i++) if (keep[i]) result.Add(points[i]);
            return result.ToArray();
        }

        private static bool EvaluateTemplateVisibility(Transform field, FieldState state)
        {
            if (cameraFrame != Time.frameCount)
            {
                cameraFrame = Time.frameCount;
                if (geometryCamera != null && geometryCamera.isActiveAndEnabled)
                    GeometryUtility.CalculateFrustumPlanes(geometryCamera, geometryPlanes);
            }
            if (geometryCamera == null || !geometryCamera.isActiveAndEnabled)
            {
                state.ScreenDiameter = float.PositiveInfinity;
                return true;
            }
            Vector3 scale = field.lossyScale;
            // A sphere is independent of stale line orientation. Include a margin for line width.
            float radius = (state.BoundsRadius + 0.1f)
                * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            foreach (Plane plane in geometryPlanes)
            {
                if (plane.GetDistanceToPoint(field.position) < -radius)
                {
                    state.GeometryVisible = false;
                    return false;
                }
            }
            float depth = Vector3.Dot(field.position - geometryCamera.transform.position,
                geometryCamera.transform.forward);
            float pixels = geometryCamera.pixelHeight;
            if (geometryCamera.orthographic)
                pixels *= radius / Mathf.Max(geometryCamera.orthographicSize, 1e-6f);
            else if (depth > radius)
                pixels *= radius / ((depth - radius)
                    * Mathf.Tan(geometryCamera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            state.ScreenDiameter = pixels;
            return true;
        }

        private static bool ShouldUpdateTemplate(Transform field, FieldState state, Quaternion rotation)
        {
            float pixels = state.ScreenDiameter;
            // Conservative projected diameter: large/near fields retain full frame rate.
            float interval = pixels >= 300f ? 0f : pixels >= 100f ? 1f / 30f
                : pixels >= 30f ? 1f / 20f : 1f / 10f;
            if (state.GeometryBlendChanged) interval = 0f;
            float now = Time.realtimeSinceStartup;
            bool returning = !state.GeometryVisible;
            state.GeometryVisible = true;
            // Rapid simulation motion bypasses throttling. Between vertex updates the caller
            // compensates the transform using the wind direction of the displayed geometry.
            if (!returning && !state.GeometryBlendChanged && now < state.NextGeometryTime
                && Quaternion.Angle(state.DisplayRotation, rotation) < 1f) return false;
            float stagger = returning ? 0.5f + (Mathf.Abs(field.GetInstanceID() % 101) / 202f) : 1f;
            state.NextGeometryTime = now + interval * stagger;
            state.DisplayRotation = rotation;
            return true;
        }

        private static Quaternion SelectCpuGeometryFrame(FieldState state, Quaternion liveRotation, Vector3 toSun)
        {
            // Deviation of the MAGNETIC axis from the plane normal to sunlight, not orbital tilt.
            float deviation = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(
                Vector3.Dot(liveRotation * Vector3.up, toSun)))) * Mathf.Rad2Deg;
            float angleLive = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(10f, 15f, deviation));
            float sizeLive = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(80f, 200f, state.ScreenDiameter));
            float target = angleLive * sizeLive;
            // Reach the endpoints exactly: the static state must stop uploading vertices.
            float previousBlend = state.LiveGeometryBlend;
            state.LiveGeometryBlend = state.HasGeometryBlend
                ? Mathf.MoveTowards(state.LiveGeometryBlend, target, Time.unscaledDeltaTime / 0.35f)
                : target;
            state.GeometryBlendChanged = !state.HasGeometryBlend
                || Mathf.Abs(previousBlend - state.LiveGeometryBlend) > 1e-6f;
            state.HasGeometryBlend = true;

            Vector3 localWind = Quaternion.Inverse(liveRotation) * -toSun;
            Quaternion tail = Quaternion.FromToRotation(Vector3.back, localWind);
            // Our own unbent template, not a second set of vanilla curves. At weight zero,
            // local -Z is the wind and the template is rigid. At one, the exact pole is restored.
            // Away from the pole/Sun singularity this is the projected-axis rigid orientation.
            Quaternion rigidRotation = liveRotation * tail;
            return Quaternion.Slerp(rigidRotation, liveRotation, state.LiveGeometryBlend);
        }

        private static void AlignCachedTail(Transform field, FieldState state, Quaternion rotation, Vector3 toSun)
        {
            if (!state.HasTailRotation) return;
            Quaternion desiredTail = Quaternion.FromToRotation(Vector3.back,
                Quaternion.Inverse(rotation) * -toSun);
            field.rotation = rotation * desiredTail * Quaternion.Inverse(state.LastTailRotation);
        }

        /// <summary>
        /// Vanilla joins two centre-outward traces and mirrors the result in Y. Some open
        /// branches cross Y=0 downstream, so each branch intersects its own mirrored copy.
        /// Repair only those branches, once, before computing the radial deformation weights.
        /// </summary>
        private static int SeparateVanillaTailBranches(Vector3[] points)
        {
            // The two zero vertices identify the join even if one trace was clipped early.
            for (int p = 1; p < points.Length; p++)
            {
                if (points[p - 1].sqrMagnitude < 1e-12f && points[p].sqrMagnitude < 1e-12f)
                {
                    return SeparateVanillaTailHalf(points, p - 1, -1, p)
                         + SeparateVanillaTailHalf(points, p, 1, points.Length - p);
                }
            }
            throw new InvalidOperationException("Vanilla field template has no centre join.");
        }

        private static int SeparateVanillaTailHalf(Vector3[] points, int start, int stride, int count)
        {
            Vector3 end = points[start + (count - 1) * stride];
            // Closed loops and short traces retain their genuine equatorial crossings.
            if (end.z >= -DeformEndRadius || end.sqrMagnitude < DeformEndRadius * DeformEndRadius)
            {
                return 0;
            }

            float side = 0f;
            float peakHeight = 0f;
            int peak = -1;
            int crossing = -1;
            for (int i = 0; i < count; i++)
            {
                Vector3 point = points[start + i * stride];
                if (side == 0f)
                {
                    if (point.sqrMagnitude < DeformStartRadius * DeformStartRadius
                        || Mathf.Abs(point.y) < 0.01f)
                    {
                        continue;
                    }
                    side = Mathf.Sign(point.y);
                }
                float height = side * point.y;
                if (height > peakHeight)
                {
                    peakHeight = height;
                    peak = i;
                }
                if (height <= 0f && point.z < -DeformStartRadius)
                {
                    crossing = i;
                    break;
                }
            }
            if (crossing < 0 || peak < 0 || peakHeight < 0.01f)
            {
                return 0;
            }

            // Start at the last lobe apex: its vertical tangent is approximately zero.
            // Ease into a separate, parallel tail instead of crossing the equatorial sheet.
            // Keep X/Z and all vertices/renderers; mirrored inputs get mirrored outputs.
            float tailHeight = Mathf.Min(peakHeight, Mathf.Max(0.1f, Mathf.Abs(end.y)));
            float length = 0f;
            for (int i = peak + 1; i < count; i++)
                length += Vector3.Distance(points[start + (i - 1) * stride], points[start + i * stride]);
            if (length < 1e-6f)
                return 0;

            Vector3 previous = points[start + peak * stride];
            float distance = 0f;
            for (int i = peak + 1; i < count; i++)
            {
                int index = start + i * stride;
                Vector3 original = points[index];
                distance += Vector3.Distance(previous, original);
                previous = original;
                float t = Mathf.Clamp01(distance / length);
                float blend = t * t * (3f - 2f * t);
                points[index].y = side * Mathf.Lerp(peakHeight, tailHeight, blend);
            }
            return 1;
        }

        /// <summary>Rotate the immutable template, with exact pole and tail directions at the ends.</summary>
        private static void DeformVanillaTemplate(FieldState state, Vector3 wind)
        {
            // Absolute shortest rotation: transporting yesterday's correction can retain an
            // arbitrary twist about the tail even after the wind returns to the same direction.
            Quaternion tail = Quaternion.FromToRotation(Vector3.back, wind);
            if (state.HasTailRotation && Quaternion.Angle(state.LastTailRotation, tail) < 0.001f)
            {
                return;
            }

            Matrix4x4 tailMatrix = Matrix4x4.Rotate(tail);

            for (int i = 0; i < state.Lines.Count; i++)
            {
                Vector3[] rest = state.Target[i];
                Vector3[] current = state.Current[i];
                float[] weights = state.TailWeights[i];
                for (int p = 0; p < rest.Length; p++)
                {
                    // Normalised quaternion interpolation avoids per-vertex trigonometry and
                    // preserves radius. Always start from rest, never the previous frame.
                    float weight = weights[p];
                    current[p] = weight <= 0f ? rest[p]
                        : weight >= 1f ? tailMatrix.MultiplyPoint3x4(rest[p])
                        : Quaternion.Lerp(Quaternion.identity, tail, weight) * rest[p];
                }
                state.Lines[i].SetPositions(current);
            }
            state.LastTailRotation = tail;
            state.LastWind = wind;
            state.HasTailRotation = true;
        }


        /// <summary>
        /// Moves the vertices of every line. Called once per frame per field while the magnetosphere is
        /// switched on — nothing is created or destroyed here, only SetPositions on existing renderers.
        /// </summary>
        internal static void UpdateFieldGeometry(Transform field, FieldState state, Vector3 toSun, string key, Quaternion rotation)
        {
            if (state.Field == null || !state.PoolReady) return;

#if TILTEDWORLDS_GPU
            if (state.Gpu != null)
            {
                try
                {
                    field.rotation = rotation;
                    Vector3 gpuWind = field.InverseTransformDirection(-toSun);
                    Quaternion tail = Quaternion.FromToRotation(Vector3.back, gpuWind);
                    state.Gpu.SetTail(tail);
                    state.LastTailRotation = tail;
                    state.LastWind = gpuWind;
                    state.HasTailRotation = true;
                }
                catch (Exception error)
                {
                    // A failed GPU update must not leave an invisible or stale magnetosphere.
                    state.Gpu.Release();
                    Destroy(state.Gpu.gameObject);
                    state.Gpu = null;
                    state.HasTailRotation = false;
                    state.GeometryVisible = false;
                    Log.LogWarning("[magnetosphere] Auto: GPU rendering unavailable; using CPU. " + error);
                }
                if (state.Gpu != null) return;
            }
#endif

            if (state.UseStaticTemplate)
            {
                // This is the same corrected/simplified template as the dynamic path, with
                // a perpendicular magnetic axis. The decision never depends on time or zoom.
                Quaternion tail = Quaternion.FromToRotation(Vector3.back,
                    Quaternion.Inverse(rotation) * -toSun);
                field.rotation = rotation * tail;
                if (!state.StaticTemplateUploaded)
                {
                    for (int i = 0; i < state.Lines.Count; i++)
                        state.Lines[i].SetPositions(state.Target[i]);
                    state.LastTailRotation = Quaternion.identity;
                    state.LastWind = Vector3.back;
                    state.HasTailRotation = true;
                    state.StaticTemplateUploaded = true;
                }
                return;
            }

            bool visible = EvaluateTemplateVisibility(field, state);
            // Hybrid rigid/live selection is parked for comparison with the prior CPU path.
            if (!visible)
            {
                AlignCachedTail(field, state, rotation, toSun);
                return;
            }
            if (!ShouldUpdateTemplate(field, state, rotation))
            {
                AlignCachedTail(field, state, rotation, toSun);
                return;
            }
            field.rotation = rotation;

            // The wind blows away from the Sun, in the field's own frame.
            Vector3 wind = field.InverseTransformDirection(-toSun);

            DeformVanillaTemplate(state, wind);
        }

    }

}
