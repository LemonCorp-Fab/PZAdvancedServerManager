# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /source

COPY PZAdvancedServerManager.sln ./
COPY src/PZAdvancedServerManager.Core/PZAdvancedServerManager.Core.csproj src/PZAdvancedServerManager.Core/
COPY src/PZAdvancedServerManager.App/PZAdvancedServerManager.App.csproj src/PZAdvancedServerManager.App/
COPY src/PZAdvancedServerManager.Cli/PZAdvancedServerManager.Cli.csproj src/PZAdvancedServerManager.Cli/
RUN dotnet restore src/PZAdvancedServerManager.App/PZAdvancedServerManager.App.csproj
RUN dotnet restore src/PZAdvancedServerManager.Cli/PZAdvancedServerManager.Cli.csproj

COPY src/PZAdvancedServerManager.Core/ src/PZAdvancedServerManager.Core/
COPY src/PZAdvancedServerManager.App/ src/PZAdvancedServerManager.App/
COPY src/PZAdvancedServerManager.Cli/ src/PZAdvancedServerManager.Cli/
RUN dotnet publish src/PZAdvancedServerManager.App/PZAdvancedServerManager.App.csproj \
    --configuration Release \
    --no-restore \
    --output /out \
    /p:UseAppHost=false
RUN dotnet publish src/PZAdvancedServerManager.Cli/PZAdvancedServerManager.Cli.csproj \
    --configuration Release \
    --no-restore \
    --output /out-cli \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime

RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        lib32gcc-s1 \
        lib32stdc++6 \
        openssh-client \
        procps \
        tini \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --gid 10001 pzasm \
    && useradd --uid 10001 --gid pzasm --create-home --shell /usr/sbin/nologin pzasm \
    && mkdir -p /app /data/home \
    && chown -R pzasm:pzasm /app /data

WORKDIR /app
COPY --from=build --chown=pzasm:pzasm /out/ ./
COPY --from=build --chown=pzasm:pzasm /out-cli/ ./cli/

ENV ASPNETCORE_URLS=http://+:5160 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    HOME=/data/home \
    PZASM_DATA_ROOT=/data \
    PZASM_STEAMCMD_AUTO_INSTALL=true

USER pzasm
EXPOSE 5160

HEALTHCHECK --interval=30s --timeout=8s --start-period=45s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:5160/health/ready > /dev/null || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "PZAdvancedServerManager.dll"]
