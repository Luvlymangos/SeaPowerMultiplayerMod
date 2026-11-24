"""Generate a Linux build project from the fork's real csproj, without NuGet.

- LiteNetLib is compiled in from source (same result as upstream's ILRepack merge:
  one DLL, no LiteNetLib reference for Anchor Chain to trip over).
- .NET Framework 4.7.2 reference assemblies come from Mono.
- Deploy/merge targets are dropped; the DLL lands in out/.
Usage: make_linux_csproj.py <repo>/src/SeapowerMultiplayer.csproj <GameDir> <out.csproj>
"""
import re, sys, os
src, game, out = sys.argv[1:4]
s = open(src, encoding='utf-8-sig').read()
srcdir = os.path.dirname(os.path.abspath(src))
# Drop package references and every custom target (ILRepack, deploy copies).
s = re.sub(r'\s*<PackageReference[^>]*/>', '', s)
s = re.sub(r'\s*<Target Name=.*?</Target>', '', s, flags=re.S)
# Windows paths -> forward slashes.
s = s.replace('\\', '/')
s = s.replace('..//lib/', '../lib/')
extra = f'''
  <PropertyGroup>
    <GameDir>{game}</GameDir>
    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>
    <FrameworkPathOverride>/usr/lib/mono/4.7.2-api</FrameworkPathOverride>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>$(DefineConstants);LITENETLIB_UNSAFE</DefineConstants>
    <OutputPath>{os.path.dirname(os.path.abspath(out))}/out/</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{srcdir}/**/*.cs" Exclude="{srcdir}/obj/**;{srcdir}/bin/**" />
    <Compile Include="/home/claude/deps/LiteNetLib/LiteNetLib/**/*.cs" Exclude="/home/claude/deps/LiteNetLib/LiteNetLib/obj/**;/home/claude/deps/LiteNetLib/LiteNetLib/bin/**" />
  </ItemGroup>
</Project>'''
s = s.replace('</Project>', extra)
# Reference HintPaths relative to the original src dir.
s = s.replace('<HintPath>../lib/', f'<HintPath>{srcdir}/../lib/')
open(out, 'w').write(s)
print("wrote", out)
