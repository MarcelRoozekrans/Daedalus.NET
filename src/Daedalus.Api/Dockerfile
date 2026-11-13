# Multi-stage build for Daedalus.Api - Optimized for Kubernetes
# Stage 1: Build with SDK. The native publish links with clang against zlib; the Alpine SDK is musl.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24 AS build
RUN apk add --no-cache clang build-base zlib-dev
# The version stamped into the assemblies (AssemblyVersion/FileVersion/InformationalVersion), passed
# by CI from GitVersion. 0.0.0 marks an image built outside CI (docs/release.md).
ARG APP_VERSION=0.0.0
WORKDIR /src

# Copy project files: every ProjectReference of Daedalus.Api, central package management and the
# feed configuration, so the restore layer caches independently of source changes
COPY ["src/Daedalus.Api/Daedalus.Api.csproj", "src/Daedalus.Api/"]
COPY ["src/Daedalus.Application/Daedalus.Application.csproj", "src/Daedalus.Application/"]
COPY ["src/Daedalus.Domain/Daedalus.Domain.csproj", "src/Daedalus.Domain/"]
COPY ["src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj", "src/Daedalus.Infrastructure/"]
COPY ["src/Daedalus.Agents/Daedalus.Agents.csproj", "src/Daedalus.Agents/"]
COPY ["src/Daedalus.ServiceDefaults/Daedalus.ServiceDefaults.csproj", "src/Daedalus.ServiceDefaults/"]
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["global.json", "global.json"]
COPY ["Directory.Packages.props", "Directory.Packages.props"]
COPY ["nuget.config", "nuget.config"]

# Restore dependencies. The runtime identifier is given here too, so the ILCompiler runtime pack for it is in the
# assets file that the --no-restore publish below reads.
RUN dotnet restore "src/Daedalus.Api/Daedalus.Api.csproj" -r linux-musl-x64

# Copy remaining source
COPY . .

# Stage 2: Publish as a Native AOT binary (Daedalus.Api.csproj sets PublishAot=true). The build stage is musl and
# so is the runtime stage below, so the executable links against the libc it runs on.
FROM build AS publish
WORKDIR "/src/src/Daedalus.Api"
# The separate debug symbols, Daedalus.Api.dbg, stay in this stage; the image ships only the binary and the content files.
RUN dotnet publish "Daedalus.Api.csproj" -c Release -r linux-musl-x64 -p:PublishAot=true --no-restore -p:Version=$APP_VERSION -o /app/publish \
 && rm -f /app/publish/*.dbg

# Stage 3: Final image - runtime-deps on Alpine, the native libraries the executable needs and no .NET runtime
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine3.24 AS final
# runtime-deps carries no ICU, and the runtime stops at start-up without it or an invariant-globalization switch.
# icu-libs keeps culture-aware behaviour instead of turning globalization off.
RUN apk add --no-cache icu-libs
WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Health check for Kubernetes
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

# Use non-root user for security
RUN addgroup -g 1001 -S appuser && adduser -S appuser -u 1001 && chown -R appuser:appuser /app
USER appuser

# The aspnet base image sets this; runtime-deps does not, and Kestrel would fall back to port 5000.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["/app/Daedalus.Api"]
