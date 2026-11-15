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

using static TiltedWorlds.BodyData;
using static TiltedWorlds.BodyOrientation;
using static TiltedWorlds.OrbitPlanes;
using static TiltedWorlds.Magnetosphere;
using static TiltedWorlds.MagnetosphereGeometry;
using static UnityEngine.Object;

namespace TiltedWorlds
{
    [BepInPlugin(PluginGuid, PluginName, VersionConstants.PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.lazyranma.tiltedworlds";
        public const string PluginName = "Tilted Worlds";

        internal static ManualLogSource Log;

        private static readonly HashSet<string> ReportedErrors = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Reports a failure once per distinct message. The same broken body would otherwise
        /// repeat its error for every view in every scene load.
        /// </summary>
        internal static void ReportError(string context, Exception error)
        {
            string message = $"{context}: {error.GetType().Name}: {error.Message}";
            if (ReportedErrors.Add(message))
            {
                Log.LogError($"{message}{Environment.NewLine}{error.StackTrace}");
            }
        }

        private static Harmony HarmonyInstance;

        private void Awake()
        {
            Log = Logger;
            DynamicMagnetospheres = Config.Bind("Magnetosphere", "DynamicFields", true,
                "Enable dynamic magnetic field deformation (GPU Auto or CPU).\n"
                + "False uses the original vanilla shape with no dynamic deformation.\n"
                + "Its magnetic axis is projected onto the plane perpendicular to sunlight.").Value;
            TiltMagneticAxes = Config.Bind("Magnetosphere", "TiltMagneticAxes", true,
                "Tilt auroras and magnetospheres off the rotation axis by each body's observed magnetic-axis offset.").Value;
#if TILTEDWORLDS_GPU
            MagnetosphereRenderer = Config.Bind("Magnetosphere", "Renderer", MagnetosphereRendererMode.Auto,
                "Auto uses the embedded GPU shader when supported, otherwise the CPU renderer.");
#endif

            // Solar Expanse destroys BepInEx_Manager at frame 0 unless the object carries
            // HideFlags.DontSave, and the resulting OnDestroy would unpatch Harmony and drop
            // the sceneLoaded handler before the first scene ever loads. A released install
            // runs with HideManagerGameObject = false, so protect the manager ourselves. This
            // is global by nature: it overrides the user's setting for every plugin.
            // Not reached for a ScriptEngine host, which already has HideAndDontSave.
            if (gameObject == Chainloader.ManagerObject && (gameObject.hideFlags & HideFlags.DontSave) == 0)
            {
                gameObject.hideFlags |= HideFlags.DontSave;
            }

            try
            {
                HarmonyInstance = new Harmony(PluginGuid);
                foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
                {
                    try
                    {
                        HarmonyInstance.CreateClassProcessor(type).Patch();
                    }
                    catch (Exception error)
                    {
                        ReportError($"Harmony patching failed for '{type.FullName}'; some plugin features may not work correctly", error);
                    }
                }
            }
            catch (Exception error)
            {
                ReportError("Harmony patching failed; some plugin features may not work correctly", error);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            LoadSaveManager.StaticOnSaveStateLoaded += OnSaveStateLoaded;

            // ScriptEngine reloads the plugin without reloading the scene. Every value the
            // plugin writes is absolute, so re-applying it over the previous instance's work is
            // a no-op — just pick up whatever is loaded right now instead of waiting for the
            // next scene transition.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    ApplyScene(scene);
                }
            }

            // A hot reload happens mid-game, when the frame offset is already available: do the
            // one-shot pass here too instead of waiting for the next save load. On a cold start
            // TimeController does not exist yet and this simply returns.
            RefreshGeometryCamera();
            if (!TryResolveEarthPole()) InitializeFields();

            Log.LogInfo($"{PluginName} v{VersionConstants.PluginVersion} loaded.");
        }

        /// <summary>
        /// Detaches every hook. ScriptEngine destroys this object and loads a new copy of the
        /// plugin in a different assembly, so the Harmony patches and the scene callback would
        /// otherwise survive and double up. The values the plugin wrote are left in place on
        /// purpose: they are absolute, and the new instance rewrites them from the same tables.
        /// </summary>
        private void OnDestroy()
        {
#if TILTEDWORLDS_GPU
            GpuMagnetosphere.Shutdown();
#endif
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                LoadSaveManager.StaticOnSaveStateLoaded -= OnSaveStateLoaded;
                SetGeometryCamera(null);

                if (HarmonyInstance != null)
                {
                    HarmonyInstance.UnpatchSelf();
                    HarmonyInstance = null;
                }
            }
            catch (Exception error)
            {
                ReportError("Teardown failed; the old hooks may survive the reload", error);
            }
        }

        /// <summary>
        /// Recalibrate Earth's pole for the save's game year once its date and orbital states
        /// have been extracted, then reapply the dependent planes.
        /// </summary>
        private static void OnSaveStateLoaded(bool success)
        {
            if (!success)
            {
                return;
            }

            try
            {
                InvalidateSceneOrientation();

                if (!TryResolveEarthPole())
                {
                    return;
                }
            }
            catch (Exception error)
            {
                ReportError("Could not recompute Earth's pole after the save loaded", error);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyScene(scene);
            try
            {
                RefreshGeometryCamera();
                // An additive scene can finish loading after Setup or clock initialization.
                // Complete its invalidated pass here if both prerequisites already exist.
                if (!TryResolveEarthPole()) InitializeFields();
            }
            catch (Exception error)
            {
                ReportError($"Could not apply orbital planes after scene '{scene.name}' loaded", error);
            }
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            RefreshGeometryCamera();
        }

        private static void ApplyScene(Scene scene)
        {
            InvalidateSceneOrientation();

            RotateObject[] rotations;
            try
            {
                // Include inactive objects: the low-orbit "fake" views are often disabled
                // until the player enters that orbit.
                rotations = Resources.FindObjectsOfTypeAll<RotateObject>();
            }
            catch (Exception error)
            {
                ReportError($"Could not enumerate the views of scene '{scene.name}'", error);
                return;
            }

            int inScene = 0;
            for (int i = 0; i < rotations.Length; i++)
            {
                RotateObject rotation = rotations[i];
                if (rotation != null && rotation.gameObject.scene == scene)
                {
                    inScene++;
                }
            }

            if (inScene == 0)
            {
                return;
            }

            foreach (RotateObject rot in rotations)
            {
                try
                {
                    TiltView(scene, rot);
                }
                catch (Exception error)
                {
                    // One broken body must not stop the remaining ones from being tilted.
                    ReportError($"Could not tilt view '{(rot == null ? "<null>" : rot.name)}'", error);
                }
            }

            try
            {
                // The ship ring is created lazily, so pick up whatever already exists.
                AlignOrbitRings();
            }
            catch (Exception error)
            {
                ReportError($"Could not align the orbit points of scene '{scene.name}'", error);
            }
        }

    }

}
