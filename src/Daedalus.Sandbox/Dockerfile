# Build context: the repository root. Dockerfile.dockerignore next to this file allow-lists what the build stage needs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY .editorconfig ./
COPY src/Daedalus.Sandbox/Daedalus.Sandbox.csproj src/Daedalus.Sandbox/
RUN dotnet restore src/Daedalus.Sandbox/Daedalus.Sandbox.csproj
COPY src/Daedalus.Sandbox/ src/Daedalus.Sandbox/
RUN dotnet publish src/Daedalus.Sandbox/Daedalus.Sandbox.csproj -c Release -o /out --no-restore

# Runtime: the SDK, because a run builds, restores and tests inside it. Nothing here is a secret.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git \
 && rm -rf /var/lib/apt/lists/*
RUN dotnet tool install RoslynCodeLens.Mcp --version 2.18.1 --tool-path /opt/tools
ENV PATH=/opt/tools:$PATH \
    ASPNETCORE_HTTP_PORTS=8080 \
    THALOS_SANDBOX_ROSLYN_COMMAND=roslyn-codelens-mcp \
    HOME=/work/home \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# The runtime mounts a fresh volume at /work, which is created from these on first mount so it starts owned by the run
# user. /work is never the working directory or the content root: a restarted host would otherwise load an
# appsettings.json an agent wrote there. The bearer token arrives only through the environment, never argv.
RUN mkdir -p /work/home /work/nuget /work/repo /work/scratch /work/import \
 && chown -R 10001:10001 /work
COPY --from=build /out /app
WORKDIR /app
USER 10001:10001
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/Daedalus.Sandbox.dll"]
