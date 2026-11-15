# Building Tilted Worlds

## Requirements

- Unity Editor 2022.3.22f1 (to build the shader).
- .NET SDK and a Solar Expanse installation (to build the plugin).
- `SOLAR_EXPANSE_DIR` set to the game directory, or `-p:GameDir=...` passed to `dotnet build`.

## Build

Run these commands from the `TiltedWorlds` directory, replacing the Unity path if needed:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe' `
    -batchmode -nographics -quit `
    -projectPath "$PWD\ShaderProject" `
    -executeMethod BuildMagnetosphereBundle.Build `
    -logFile "$PWD\ShaderBundle-build.log"

dotnet build TiltedWorlds.csproj -c Release
```

The first command creates `ShaderBundle\magnetosphere.bundle` for Windows x64
(Direct3D 11). The plugin build embeds this bundle and deploys
the DLL and PDB to `BepInEx\scripts\` to be loaded by ScriptEngine. You may want to
change the deployment path in `TiltedWorlds.csproj` to `BepInEx\plugins\` if
you don't use ScriptEngine.

For a CPU-only build, pass `-p:EnableGpuMagnetospheres=false` to `dotnet build`.
