# .NET runtime/build image
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS base
WORKDIR /app

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
# The version stamped into the assemblies, passed by CI from GitVersion. 0.0.0 = built outside CI.
ARG APP_VERSION=0.0.0
WORKDIR /src

# Every ProjectReference of Daedalus.Console, central package management and the feed configuration,
# so the restore layer caches independently of source changes
COPY ["src/Daedalus.Console/Daedalus.Console.csproj", "src/Daedalus.Console/"]
COPY ["src/Daedalus.Application/Daedalus.Application.csproj", "src/Daedalus.Application/"]
COPY ["src/Daedalus.Domain/Daedalus.Domain.csproj", "src/Daedalus.Domain/"]
COPY ["src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj", "src/Daedalus.Infrastructure/"]
COPY ["src/Daedalus.Agents/Daedalus.Agents.csproj", "src/Daedalus.Agents/"]
COPY ["src/Daedalus.ServiceDefaults/Daedalus.ServiceDefaults.csproj", "src/Daedalus.ServiceDefaults/"]
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["Directory.Packages.props", "Directory.Packages.props"]
COPY ["nuget.config", "nuget.config"]
RUN dotnet restore "src/Daedalus.Console/Daedalus.Console.csproj"

COPY . .
WORKDIR "/src/src/Daedalus.Console"
RUN dotnet build "Daedalus.Console.csproj" -c Release --no-restore -o /app/build -p:Version=$APP_VERSION

# Publish stage
FROM build AS publish
RUN dotnet publish "Daedalus.Console.csproj" -c Release --no-restore -o /app/publish -p:Version=$APP_VERSION

# Runtime stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Daedalus.Console.dll"]
