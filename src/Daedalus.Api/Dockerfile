# Multi-stage build for Daedalus.Api - Optimized for Kubernetes
# Stage 1: Build with SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
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
COPY ["Directory.Packages.props", "Directory.Packages.props"]
COPY ["nuget.config", "nuget.config"]
COPY ["packages-local/", "packages-local/"]

# Restore dependencies
RUN dotnet restore "src/Daedalus.Api/Daedalus.Api.csproj"

# Copy remaining source
COPY . .

# Build the project
WORKDIR "/src/src/Daedalus.Api"
RUN dotnet build "Daedalus.Api.csproj" -c Release --no-restore -o /app/build -p:Version=$APP_VERSION

# Stage 2: Publish — framework-dependent, onto the official ASP.NET runtime image below. (Native AOT
# and a self-contained glibc linux-x64 publish onto Alpine/musl were the previous shape; neither ever
# built or ran — see the note in Daedalus.Api.csproj.)
FROM build AS publish
RUN dotnet publish "Daedalus.Api.csproj"     -c Release     --no-restore     -p:Version=$APP_VERSION     -o /app/publish

# Stage 3: Final image - ASP.NET Core runtime on Alpine, minimal footprint for Kubernetes
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Health check for Kubernetes
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3     CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

# Use non-root user for security
RUN addgroup -g 1001 -S appuser &&     adduser -S appuser -u 1001 &&     chown -R appuser:appuser /app
USER appuser

EXPOSE 8080
ENTRYPOINT ["dotnet", "Daedalus.Api.dll"]
