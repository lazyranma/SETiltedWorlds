using System;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildMagnetosphereBundle
{
    // Unity.exe -batchmode -nographics -quit -projectPath ShaderProject
    //           -executeMethod BuildMagnetosphereBundle.Build -logFile shader-build.log
    public static void Build()
    {
        const string assetPath = "Assets/TiltedWorldsMagnetosphere.shader";
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
        if (shader == null) throw new InvalidOperationException("Shader source could not be imported.");
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { GraphicsDeviceType.Direct3D11 });
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../ShaderBundle"));
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = "magnetosphere.bundle",
            assetNames = new[] { assetPath }
        };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression
                | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new InvalidOperationException("AssetBundle build failed.");
        foreach (ShaderMessage message in ShaderUtil.GetShaderMessages(shader))
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                throw new InvalidOperationException(message.message);
        var check = AssetBundle.LoadFromFile(Path.Combine(output, "magnetosphere.bundle"));
        if (check == null) throw new InvalidOperationException("Built bundle failed its load check.");
        if (check.LoadAsset<Shader>(assetPath) == null)
            throw new InvalidOperationException("Built bundle does not contain the shader.");
        check.Unload(true);
        Debug.Log("TiltedWorlds GPU bundle ready: " + output);
    }
}
