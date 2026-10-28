# Build context: the repository root. Dockerfile.dockerignore next to this file allow-lists what the build stage needs.
# The build stage publishes the host as a Native AOT binary, phase 3.1, which links with clang against zlib.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
# The version stamped into the binary, passed by CI from GitVersion. 0.0.0 marks an image built outside CI
# (docs/release.md).
ARG APP_VERSION=0.0.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY .editorconfig ./
COPY src/Daedalus.Sandbox/Daedalus.Sandbox.csproj src/Daedalus.Sandbox/
RUN dotnet restore src/Daedalus.Sandbox/Daedalus.Sandbox.csproj -r linux-x64
COPY src/Daedalus.Sandbox/ src/Daedalus.Sandbox/
# The separate debug symbols, Daedalus.Sandbox.dbg, stay in this stage; the image ships only the binary.
RUN dotnet publish src/Daedalus.Sandbox/Daedalus.Sandbox.csproj -c Release -r linux-x64 -p:PublishAot=true -p:Version=$APP_VERSION -o /out --no-restore \
 && rm -f /out/*.dbg

# Runtime: the SDK, because a run builds, restores and tests inside it; the native host itself needs no .NET runtime.
# Nothing here is a secret.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403
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
ENTRYPOINT ["/app/Daedalus.Sandbox"]
