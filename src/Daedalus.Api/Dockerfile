# Multi-stage build for Daedalus.Api - Optimized for Kubernetes
# Stage 1: Build with SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files
COPY ["src/Daedalus.Api/Daedalus.Api.csproj", "src/Daedalus.Api/"]
COPY ["src/Daedalus.Application/Daedalus.Application.csproj", "src/Daedalus.Application/"]
COPY ["src/Daedalus.Domain/Daedalus.Domain.csproj", "src/Daedalus.Domain/"]
COPY ["src/Daedalus.Infrastructure/Daedalus.Infrastructure.csproj", "src/Daedalus.Infrastructure/"]
COPY ["src/Daedalus.ServiceDefaults/Daedalus.ServiceDefaults.csproj", "src/Daedalus.ServiceDefaults/"]
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["Directory.Packages.props", "Directory.Packages.props"]

# Restore dependencies
RUN dotnet restore "src/Daedalus.Api/Daedalus.Api.csproj"

# Copy remaining source
COPY . .

# Build the project
WORKDIR "/src/src/Daedalus.Api"
RUN dotnet build "Daedalus.Api.csproj" -c Release --no-restore -o /app/build

# Stage 2: Publish with native AOT compilation
FROM build AS publish
RUN dotnet publish "Daedalus.Api.csproj" \
    -c Release \
    --no-restore \
    -o /app/publish \
    -r linux-x64 \
    --self-contained

# Stage 3: Runtime - Alpine with libc6 compat for .NET
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
RUN apk add --no-cache libstdc++ libgomp libgfortran
WORKDIR /app

# Stage 4: Final image - minimal footprint for Kubernetes
FROM runtime AS final
WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Health check for Kubernetes
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

# Use non-root user for security
RUN addgroup -g 1001 -S appuser && \
    adduser -S appuser -u 1001 && \
    chown -R appuser:appuser /app
USER appuser

EXPOSE 8080
ENTRYPOINT ["./Daedalus.Api"]
