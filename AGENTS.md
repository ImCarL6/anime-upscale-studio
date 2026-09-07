# Working on Anime Upscale Studio

- This is the source repository. Native tools/models live outside Git; see docs/DEPENDENCIES.md.
- Do not commit videos, logs, pilot reports, machine-specific paths, engines, timing caches, secrets or signing material.
- Preserve source media and existing recovery caches. No automatic cache deletion.
- Update CHANGELOG.md under Próxima versão for every bug fix or feature. A build is not a published release.
- Build: `dotnet restore src/Anime4KEncoder/Anime4KEncoder.csproj -r win-x64 --locked-mode`, then `dotnet build src/Anime4KEncoder/Anime4KEncoder.csproj -c Release -r win-x64 --no-restore`.
- Checks: `dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj`. Video tests require supplied authorized fixtures and NVIDIA GPU; see docs/TESTING.md.
- Keep Velopack SDK and CLI pinned to the same version. Bootstrap must run before WPF; do not enable automatic apply on startup or apply during a queue/pilot.
- Installed user data must remain outside Velopack's installation directory. Preserve model/engine namespace for app-only releases; invalidate it for runtime/model changes.
- Public releases need the actual installer, package and feed to be validated. Do not claim clean-Windows/RTX 5070/update-cycle testing based only on a build.
- No general source license has been chosen. Do not invent one. Check third-party provenance before distributing full binary releases.
