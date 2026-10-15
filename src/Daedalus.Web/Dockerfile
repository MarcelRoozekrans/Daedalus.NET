# Multi-stage build for Daedalus.Web - Blazor WASM frontend
# Stage 1: Build the Blazor WASM project
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
# The version stamped into the assemblies, passed by CI from GitVersion. 0.0.0 = built outside CI.
ARG APP_VERSION=0.0.0
WORKDIR /src

# Copy project files (plus central package management and the feed configuration)
COPY ["src/Daedalus.Web/Daedalus.Web.csproj", "src/Daedalus.Web/"]
COPY ["src/Daedalus.Application/Daedalus.Application.csproj", "src/Daedalus.Application/"]
COPY ["src/Daedalus.Domain/Daedalus.Domain.csproj", "src/Daedalus.Domain/"]
COPY ["src/Daedalus.ServiceDefaults/Daedalus.ServiceDefaults.csproj", "src/Daedalus.ServiceDefaults/"]
COPY ["Directory.Build.props", "Directory.Build.props"]
COPY ["Directory.Packages.props", "Directory.Packages.props"]
COPY ["nuget.config", "nuget.config"]

# Restore dependencies
RUN dotnet restore "src/Daedalus.Web/Daedalus.Web.csproj"

# Copy remaining source
COPY . .

# Build the project
WORKDIR "/src/src/Daedalus.Web"
RUN dotnet build "Daedalus.Web.csproj" -c Release --no-restore -o /app/build -p:Version=$APP_VERSION

# Stage 2: Publish WASM build
FROM build AS publish
RUN dotnet publish "Daedalus.Web.csproj" \
    -c Release \
    --no-restore \
    -p:Version=$APP_VERSION \
    -o /app/publish

# Stage 3: Final image - serve static WASM files
FROM nginx:alpine-slim AS final
WORKDIR /app

# Copy published WASM app to nginx wwwroot
COPY --from=publish /app/publish/wwwroot /usr/share/nginx/html

# Copy nginx configuration
COPY <<EOF /etc/nginx/conf.d/default.conf
server {
    listen 8080;
    server_name _;
    
    root /usr/share/nginx/html;
    index index.html;
    
    # Cache control - static assets
    location / {
        try_files $uri $uri/ /index.html =404;
        expires 1y;
        add_header Cache-Control "public, immutable";
    }
    
    # WASM files - longer cache
    location ~* \.wasm$ {
        add_header Content-Type "application/wasm";
        add_header Cache-Control "public, max-age=31536000, immutable";
        expires 365d;
    }
    
    # JavaScript - medium cache
    location ~* \.js$ {
        add_header Cache-Control "public, max-age=3600";
        expires 1h;
    }
    
    # index.html - no cache (always check for updates)
    location = /index.html {
        add_header Cache-Control "public, max-age=0, must-revalidate";
        expires 0;
    }
    
    # Health check endpoint
    location /health {
        return 200 "healthy\n";
        add_header Content-Type text/plain;
    }
}
EOF

# Health check for Kubernetes
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

EXPOSE 8080
