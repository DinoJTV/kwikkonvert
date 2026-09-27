# Contributing to KwikKonvert

Bug reports, usability feedback and focused pull requests are welcome.

## Set up

Use Windows 10 1809+ or Windows 11 with a .NET SDK capable of building the .NET 8 projects. Open `KwikKonvert.sln` in Visual Studio, or follow the build commands in the README. The core project and its tests do not depend on Windows APIs.

```powershell
dotnet run --project tests/KwikKonvert.Core.Tests -c Release
dotnet build src/KwikKonvert/KwikKonvert.csproj -c Release -p:Platform=x64 -r win-x64
```

## Before a pull request

- Describe the user-visible problem and the change that fixes it.
- Keep changes focused, and add a regression test when changing core behaviour.
- Run the core tests and the Windows build. Describe any manual codec or interface checks.
- Preserve originals, avoid overwriting existing output, and clean up incomplete conversion results.
- Keep generated binaries, local settings, logs and private sample files out of commits.

For interface changes, include a screenshot using non-sensitive sample filenames. For codec issues, include the file format, installed codec information and reproducible steps. Check sample files and screenshots for personal information before attaching them.

## Repository conventions

Core logic belongs in `src/KwikKonvert.Core`; Windows-specific codecs and interface code belong in `src/KwikKonvert`. The tests use an executable test runner, so run them with `dotnet run`, not only `dotnet test`.

This repository does not currently include a software license. Public visibility alone does not grant a general license to reuse or redistribute its code or artwork.
