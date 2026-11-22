# syntax=docker/dockerfile:1

# ---- web interface (Vue + Vuetify) ----
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# vite.config.js writes to ../src/Daikin2Mqtt.Service/wwwroot
RUN npm run build

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (better layer caching)
COPY daikin2mqtt.sln ./
COPY src/Daikin2Mqtt.Core/Daikin2Mqtt.Core.csproj src/Daikin2Mqtt.Core/
COPY src/Daikin2Mqtt.Daikin/Daikin2Mqtt.Daikin.csproj src/Daikin2Mqtt.Daikin/
COPY src/Daikin2Mqtt.Service/Daikin2Mqtt.Service.csproj src/Daikin2Mqtt.Service/
COPY tests/Daikin2Mqtt.Tests/Daikin2Mqtt.Tests.csproj tests/Daikin2Mqtt.Tests/
RUN dotnet restore src/Daikin2Mqtt.Service/Daikin2Mqtt.Service.csproj

COPY src/ src/
COPY --from=web /src/Daikin2Mqtt.Service/wwwroot src/Daikin2Mqtt.Service/wwwroot
RUN dotnet publish src/Daikin2Mqtt.Service/Daikin2Mqtt.Service.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Configuration: mount a folder holding config.yml and entities.yml on /config (see docker-compose.yml).
# It must be writable by the container user for the web interface to save. The web interface listens on
# Web__Port (default 8080), together with the live monitor (SignalR).
EXPOSE 8080
ENV DOTNET_EnableDiagnostics=0

# Runs as the non-root user provided by the base image
USER $APP_UID
ENTRYPOINT ["dotnet", "Daikin2Mqtt.Service.dll"]
