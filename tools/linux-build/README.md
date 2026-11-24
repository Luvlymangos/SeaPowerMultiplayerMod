# Linux build (no NuGet)

Used to build the fork in a sandbox where NuGet is blocked but apt and GitHub work.

1. `apt-get install -y dotnet-sdk-8.0 mono-devel`
2. `git clone --depth 1 --branch 1.3.5-upm https://github.com/RevenantX/LiteNetLib /home/claude/deps/LiteNetLib`
   (compiled in from source - same effect as upstream's ILRepack merge)
3. Put the game DLLs in `<GameDir>/Sea Power_Data/Managed/` and `<GameDir>/BepInEx/core/`
   (the HintPaths in src/SeapowerMultiplayer.csproj list exactly which).
4. ```
   mkdir -p /home/claude/build/fork && cp tools/linux-build/NuGet.config /home/claude/build/
   python3 tools/linux-build/make_linux_csproj.py src/SeapowerMultiplayer.csproj "<GameDir>" /home/claude/build/fork/SeapowerMultiplayer.Linux.csproj
   cd /home/claude/build/fork && dotnet build SeapowerMultiplayer.Linux.csproj
   ```
   Output: `/home/claude/build/fork/out/SeapowerMultiplayer.dll`. Ship it with `workshop/_info.ini`.

The script's LiteNetLib path is fixed at /home/claude/deps/LiteNetLib.
