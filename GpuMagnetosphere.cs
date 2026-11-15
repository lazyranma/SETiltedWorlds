using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace TiltedWorlds
{
    // Owns every native resource so ScriptEngine can unload without leaving old renderers alive.
    public sealed class GpuMagnetosphere : MonoBehaviour
    {
        private const string BundleResource = "TiltedWorlds.magnetosphere.bundle";
        private static readonly HashSet<GpuMagnetosphere> Active = new HashSet<GpuMagnetosphere>();
        private static readonly int TailId = Shader.PropertyToID("_TailRotation");
        private static readonly int ScaleId = Shader.PropertyToID("_LengthScale");
        private static readonly int WidthId = Shader.PropertyToID("_WidthMultiplier");
        private static bool attempted;
        private static AssetBundle bundle;
        private static Shader shader;
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<LineRenderer> originals = new List<LineRenderer>();
        private readonly List<bool> originalActive = new List<bool>();
        private Material material;
        private TrailWidthScaler widthScaler;
        private float boundsRadius;
        private float maxHalfWidth;
        private float boundsPadding;
        private bool disposed;

        private static bool LoadShader()
        {
            if (attempted) return shader != null && shader.isSupported;
            attempted = true;
            try
            {
                using (Stream stream = typeof(GpuMagnetosphere).Assembly.GetManifestResourceStream(BundleResource))
                {
                    if (stream == null) throw new InvalidOperationException("Embedded shader bundle is missing.");
                    using (var bytes = new MemoryStream())
                    {
                        stream.CopyTo(bytes);
                        bundle = AssetBundle.LoadFromMemory(bytes.ToArray());
                    }
                }
                if (bundle == null) throw new InvalidOperationException("Unity could not load the shader bundle.");
                shader = bundle.LoadAsset<Shader>("Assets/TiltedWorldsMagnetosphere.shader");
                if (shader == null || !shader.isSupported)
                    throw new NotSupportedException("Magnetosphere shader is unsupported on " + SystemInfo.graphicsDeviceType);
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("[magnetosphere] Auto: GPU rendering unavailable; using CPU. " + error.Message);
                shader = null;
                if (bundle != null) { bundle.Unload(true); bundle = null; }
                return false;
            }
        }

        internal static GpuMagnetosphere TryCreate(Transform parent, List<LineRenderer> lines,
            List<Vector3[]> points, List<float[]> weights, float radius)
        {
            if (lines.Count == 0 || !LoadShader()) return null;
            GameObject root = null;
            GpuMagnetosphere owner = null;
            try
            {
                root = new GameObject("TiltedWorlds GPU Magnetosphere");
                root.SetActive(false);
                root.transform.SetParent(parent, false);
                owner = root.AddComponent<GpuMagnetosphere>();
                owner.boundsRadius = radius;
                owner.widthScaler = lines[0].GetComponent<TrailWidthScaler>();
                if (owner.widthScaler != null && !owner.widthScaler.enabled) owner.widthScaler = null;
                Active.Add(owner);
                owner.material = new Material(shader) { name = "TiltedWorlds GPU Field", hideFlags = HideFlags.DontSave };
                Material source = lines[0].sharedMaterial;
                // Preserve the actual vanilla texture, tint and animation parameters.
                foreach (string name in new[] { "_OpacityTiling", "_OpacityPower" })
                    if (source.HasProperty(name)) owner.material.SetFloat(name, source.GetFloat(name));
                foreach (string name in new[] { "_ColorMultiply", "_AnimationSpeed" })
                    if (source.HasProperty(name)) owner.material.SetVector(name, source.GetVector(name));
                if (source.HasProperty("_OpacityTexture"))
                {
                    owner.material.SetTexture("_OpacityTexture", source.GetTexture("_OpacityTexture"));
                    owner.material.SetTextureScale("_OpacityTexture", source.GetTextureScale("_OpacityTexture"));
                    owner.material.SetTextureOffset("_OpacityTexture", source.GetTextureOffset("_OpacityTexture"));
                }
                owner.material.SetVector(TailId, new Vector4(0f, 0f, 0f, 1f));
                owner.material.renderQueue = source.renderQueue;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].sharedMaterial != source)
                        throw new NotSupportedException("Mixed field materials require the CPU renderer.");
                    var scaler = lines[i].GetComponent<TrailWidthScaler>();
                    if (scaler != null && !scaler.enabled) scaler = null;
                    if ((scaler == null) != (owner.widthScaler == null)
                        || (scaler != null && (scaler.distanceDivide != owner.widthScaler.distanceDivide
                            || scaler.minWidth != owner.widthScaler.minWidth || scaler.maxWidth != owner.widthScaler.maxWidth
                            || (scaler.transform.position - owner.widthScaler.transform.position).sqrMagnitude > 1e-12f)))
                        throw new NotSupportedException("Mixed field width scaling requires the CPU renderer.");
                    owner.BuildLine(lines[i], points[i], weights[i], radius);
                    owner.originals.Add(lines[i]);
                    owner.originalActive.Add(lines[i].gameObject.activeSelf);
                }
                owner.UpdateWidth();
                // Only switch renderers after every mesh and material has been created successfully.
                // The game's visibility code can re-enable Renderer.enabled. Keep the CPU
                // objects inactive for the entire GPU lifetime, not just their renderers.
                foreach (LineRenderer line in owner.originals)
                {
                    line.enabled = false;
                    line.gameObject.SetActive(false);
                }
                root.SetActive(true);
                return owner;
            }
            catch (Exception error)
            {
                if (owner != null) owner.Release();
                if (root != null) Destroy(root);
                Plugin.Log.LogWarning("[magnetosphere] Auto: GPU rendering unavailable; using CPU. " + error);
                return null;
            }
        }

        private void BuildLine(LineRenderer source, Vector3[] points, float[] weights, float radius)
        {
            if (source.loop || source.alignment != LineAlignment.View
                || (source.textureMode != LineTextureMode.Tile && source.textureMode != LineTextureMode.Stretch))
                throw new NotSupportedException("Unsupported vanilla line settings.");
            int count = points.Length;
            var vertices = new Vector3[count * 2];
            var colors = new Color[count * 2];
            var parameters = new List<Vector4>(count * 2);
            var previous = new List<Vector4>(count * 2);
            var next = new List<Vector4>(count * 2);
            var triangles = new int[(count - 1) * 6];
            var distance = new float[count];
            for (int i = 1; i < count; i++) distance[i] = distance[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            float total = Mathf.Max(distance[count - 1], 1e-6f);
            AnimationCurve width = source.widthCurve;
            Gradient gradient = source.colorGradient;
            for (int i = 0; i < count; i++)
            {
                int before = i - 1;
                int after = i + 1;
                while (before >= 0 && (points[before] - points[i]).sqrMagnitude < 1e-12f) before--;
                while (after < count && (points[after] - points[i]).sqrMagnitude < 1e-12f) after++;
                before = Mathf.Max(0, before);
                after = Mathf.Min(count - 1, after);
                float t = distance[i] / total;
                // TrailWidthScaler overwrites both endpoints with the same world-space width.
                // Its objects stay inactive on GPU; apply that width once through the material.
                float halfWidth = widthScaler != null ? 0.5f
                    : Mathf.Abs(width.Evaluate(t) * source.widthMultiplier) * 0.5f;
                maxHalfWidth = Mathf.Max(maxHalfWidth, halfWidth);
                float u = source.textureMode == LineTextureMode.Tile ? distance[i] : t;
                for (int side = 0; side < 2; side++)
                {
                    vertices[i * 2 + side] = points[i];
                    colors[i * 2 + side] = gradient.Evaluate(t);
                    parameters.Add(new Vector4(u, side == 0 ? -1f : 1f, weights[i], halfWidth));
                    previous.Add(new Vector4(points[before].x, points[before].y, points[before].z, weights[before]));
                    next.Add(new Vector4(points[after].x, points[after].y, points[after].z, weights[after]));
                }
                if (i == count - 1) continue;
                int v = i * 2;
                int k = i * 6;
                triangles[k] = v; triangles[k + 1] = v + 1; triangles[k + 2] = v + 2;
                triangles[k + 3] = v + 2; triangles[k + 4] = v + 1; triangles[k + 5] = v + 3;
            }
            var mesh = new Mesh { name = "GPU field strip", hideFlags = HideFlags.DontSave };
            meshes.Add(mesh);
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.SetUVs(0, parameters);
            mesh.SetUVs(1, previous);
            mesh.SetUVs(2, next);
            mesh.triangles = triangles;
            // Deformation preserves radius. Pad for the world-space ribbon width as well.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * radius));
            mesh.UploadMeshData(true);
            var child = new GameObject("GPU field line");
            child.layer = source.gameObject.layer;
            child.transform.SetParent(transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder;
        }

        internal void SetTail(Quaternion tail)
        {
            // Match Quaternion.Lerp's shortest-arc convention when blending from identity.
            if (tail.w < 0f) tail = new Quaternion(-tail.x, -tail.y, -tail.z, -tail.w);
            material.SetVector(TailId, new Vector4(tail.x, tail.y, tail.z, tail.w));
            Vector3 scale = transform.lossyScale;
            material.SetFloat(ScaleId, Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            UpdateWidth();
        }

        private void UpdateWidth()
        {
            float width = 1f;
            if (widthScaler != null)
            {
                Camera widthCamera = Magnetosphere.geometryCamera;
                width = originals[0].startWidth;
                if (widthCamera != null && widthCamera.isActiveAndEnabled)
                {
                    float distanceWidth = Vector3.Distance(widthCamera.transform.position,
                        widthScaler.transform.position) / widthScaler.distanceDivide;
                    // Match TrailWidthScaler's cap exactly, including minWidth below the cap.
                    width = widthScaler.maxWidth != 0f && distanceWidth > widthScaler.maxWidth
                        ? widthScaler.maxWidth : widthScaler.minWidth + distanceWidth;
                }
            }
            material.SetFloat(WidthId, Mathf.Abs(width));
            Vector3 scale = transform.lossyScale;
            float minScale = Mathf.Max(1e-6f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            float padding = maxHalfWidth * Mathf.Abs(width) / minScale + 0.1f;
            // Width is in world units, while bounds are local. Grow only when necessary;
            // camera movement never rebuilds or uploads the ribbon vertices.
            if (padding > boundsPadding)
            {
                boundsPadding = padding;
                var bounds = new Bounds(Vector3.zero, Vector3.one * (2f * (boundsRadius + padding)));
                foreach (Mesh mesh in meshes) mesh.bounds = bounds;
            }
        }

        internal void Release()
        {
            if (disposed) return;
            disposed = true;
            gameObject.SetActive(false);
            for (int i = 0; i < originals.Count; i++)
            {
                LineRenderer line = originals[i];
                if (line == null) continue;
                line.enabled = true;
                line.gameObject.SetActive(originalActive[i]);
            }
            foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
            if (material != null) Destroy(material);
            Active.Remove(this);
        }

        private void OnDestroy() { Release(); }

        internal static void Shutdown()
        {
            foreach (GpuMagnetosphere owner in new List<GpuMagnetosphere>(Active))
            {
                if (owner == null) continue;
                owner.Release();
                Destroy(owner.gameObject);
            }
            Active.Clear();
            if (bundle != null) bundle.Unload(true);
            bundle = null;
            shader = null;
            attempted = false;
        }
    }
}
