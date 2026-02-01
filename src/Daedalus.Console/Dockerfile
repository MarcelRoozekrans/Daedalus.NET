# .NET runtime/build image
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS base
WORKDIR /app

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/Daedalus.Console/Daedalus.Console.csproj", "src/Daedalus.Console/"]
RUN dotnet restore "src/Daedalus.Console/Daedalus.Console.csproj"

COPY . .
WORKDIR "/src/src/Daedalus.Console"
RUN dotnet build "Daedalus.Console.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Daedalus.Console.csproj" -c Release -o /app/publish

# Runtime stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Daedalus.Console.dll"]
