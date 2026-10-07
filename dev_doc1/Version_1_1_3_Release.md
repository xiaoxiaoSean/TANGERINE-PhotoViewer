# Version 1.1.3 release

The project version is declared by `<Version>` in `TANGERINE-PhotoViewer/TANGERINE-PhotoViewer.csproj`. The About dialog reads the compiled assembly version, so it displays 1.1.3 without a separate text change.

Publish `Properties/PublishProfiles/FolderProfile.pubxml` for the framework-dependent Windows x64 single-file build and `FolderProfile1.pubxml` for the self-contained Windows x64 single-file build. The resulting executables are copied to `TPV-out/fd/TPV-v1.1.3-fd.exe` and `TPV-out/sc/TPV-v1.1.3-sc.exe`. Both executable version resources report 1.1.3.0.

Commands:

```powershell
dotnet publish TANGERINE-PhotoViewer/TANGERINE-PhotoViewer.csproj -p:PublishProfile=FolderProfile -c Release
dotnet publish TANGERINE-PhotoViewer/TANGERINE-PhotoViewer.csproj -p:PublishProfile=FolderProfile1 -c Release
```

No smoke test was run, as requested.
