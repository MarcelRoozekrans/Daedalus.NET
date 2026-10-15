# Vendored NuGet packages (temporary)

Tracked by [Daedalus.NET#361](https://github.com/MarcelRoozekrans/Daedalus.NET/issues/361).

Minos.NET 1.0.1 is not on nuget.org yet; it is with the NuGet team. Until it is, its three packages that Daedalus
restores are kept here so CI and the Docker images can build:

- `Minos.NET.1.0.1.nupkg`
- `Minos.NET.SystemOne.1.0.1.nupkg`
- `Minos.NET.DependencyInjection.1.0.1.nupkg`

They were packed from the Minos.NET `v1.0.1` tag with `dotnet pack -c Release -p:MinosRelease=true`.
`nuget.config` maps `Minos.NET*` to this folder only; every other package comes from nuget.org.

**What this hides:** CI builds against these packed binaries, not the published package. If the published 1.0.1 differs,
CI will not see it until this folder is removed.

**Remove it** once `curl -s https://api.nuget.org/v3-flatcontainer/minos.net.systemone/index.json` lists `1.0.1`:

1. Delete this folder.
2. Remove the `vendored` source and its mapping from `nuget.config`.
3. Remove the `!vendor/nuget/*.nupkg` line from `.gitignore`.
4. Remove the `vendor/nuget/` copy from `src/Daedalus.Api/Dockerfile`, `src/Daedalus.Web/Dockerfile` and
   `src/Daedalus.Sandbox/Dockerfile`, and the `!vendor/nuget/` line from `src/Daedalus.Sandbox/Dockerfile.dockerignore`.
5. Delete `~/.nuget/packages/minos.net*/1.0.1` on any machine that restored from here, then `dotnet restore`.
