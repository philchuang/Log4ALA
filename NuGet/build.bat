SET MSBUILD="C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\Bin\MSBuild.exe"

%MSBUILD% build.proj /target:NuGetRestore
%MSBUILD% /p:Configuration=Release ..\Log4ALA.sln
%MSBUILD% build.proj /target:NuGetPack /property:Configuration=Release;RELEASE=true
REM PackageVersion=4.5.0;PatchVersion=0;PatchCoreVersion=0

pause