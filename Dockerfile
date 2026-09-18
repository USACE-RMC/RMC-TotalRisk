# ============================================================================
# RMC-TotalRisk API deployment image
# ============================================================================
# Multi-stage build for the RMC-TotalRisk REST + MCP compute service.
#
# Build context is the repository root, because the shared build inputs live there:
#   <context>/
#     Directory.Build.props, Directory.Packages.props, global.json, nuget.cwbi.config
#     local-feed/                 (prerelease RMC.Numerics package until 2.2.0 ships)
#     src/RMC.TotalRisk/          (the model library)
#     src/RMC.TotalRisk.Api/      (the service)
#
# Build (from the repository root):
#   docker build -t dst-total-risk .
#
# The CWBI release pipeline passes the three provenance arguments below and verifies the
# resulting labels, user, port, healthcheck and filesystem (.github/scripts/Verify-TotalRiskImage.ps1).
# A plain `docker build .` still works; the labels then carry placeholder values.
#
# Image defaults match the CWBI dst-total-risk ECS service: HTTP on port 8083 under path
# base /total-risk. TLS terminates in front of the container. Override with
# -e PathBase= / -e ASPNETCORE_URLS=... for local use.
#
# Base images are pinned by digest so a rebuild of the same commit yields the same runtime and
# the verifier can compare the runtime trust store against the exact pinned base.
# ============================================================================

ARG SOURCE_REPOSITORY=https://github.com/USACE-RMC/RMC-TotalRisk
ARG SOURCE_REVISION=0000000000000000000000000000000000000000
ARG SNAPSHOT_REVISION=not-published

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8 AS base
WORKDIR /app
EXPOSE 8083

ENV ASPNETCORE_URLS=http://+:8083
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV PathBase=/total-risk

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore inputs first so the package layer is cached independently of source edits.
# nuget.cwbi.config lists only the in-repo feed and nuget.org (the root NuGet.config also
# names a developer-machine feed that does not exist here).
COPY ["nuget.cwbi.config", "Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["local-feed/", "local-feed/"]
COPY ["src/RMC.TotalRisk/RMC.TotalRisk.csproj", "src/RMC.TotalRisk/packages.lock.json", "src/RMC.TotalRisk/"]
COPY ["src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj", "src/RMC.TotalRisk.Api/packages.lock.json", "src/RMC.TotalRisk.Api/"]

RUN dotnet restore "src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj" \
    --locked-mode \
    --configfile /src/nuget.cwbi.config \
    --verbosity normal

COPY ["src/RMC.TotalRisk/", "src/RMC.TotalRisk/"]
COPY ["src/RMC.TotalRisk.Api/", "src/RMC.TotalRisk.Api/"]

RUN dotnet build "src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/build \
    --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "src/RMC.TotalRisk.Api/RMC.TotalRisk.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM base AS final
ARG SOURCE_REPOSITORY
ARG SOURCE_REVISION
ARG SNAPSHOT_REVISION
WORKDIR /app

LABEL org.opencontainers.image.source=$SOURCE_REPOSITORY \
      org.opencontainers.image.revision=$SOURCE_REVISION \
      mil.army.usace.cwbi.snapshot-revision=$SNAPSHOT_REVISION

COPY --from=publish /app/publish .

RUN adduser --disabled-password --gecos "" --home /app appuser && \
    chown -R appuser:appuser /app
USER appuser

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://localhost:8083/total-risk/health || exit 1

ENTRYPOINT ["dotnet", "RMC.TotalRisk.Api.dll"]
