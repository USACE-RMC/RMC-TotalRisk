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
# The runtime runs on CWBI's Chainguard FIPS base image, which CWBI requires for deployed
# containers. Pulling cgr.dev/usace-cwbi needs a Chainguard login first: `docker login cgr.dev`
# with a personal pull token locally, and the cwbi-apps org secrets CGR_USERNAME / CGR_PASSWORD
# in the workflow. Local development runs the service with `dotnet run` and never builds this image.
#
# The Chainguard base ships without .NET, libstdc++, ICU, time zone data and curl (the
# healthcheck client; the base has no wget). They come from CWBI's Chainguard package
# repository at build time, so a rebuild picks up that repository's current patch versions.
#
# Base images and packages are deliberately unpinned: CWBI's security scans expect regular
# rebuilds, and each rebuild should pick up the latest patched base and packages without
# hand-edited digests.
# ============================================================================

ARG SOURCE_REPOSITORY=https://github.com/USACE-RMC/RMC-TotalRisk
ARG SOURCE_REVISION=0000000000000000000000000000000000000000
ARG SNAPSHOT_REVISION=not-published

FROM cgr.dev/usace-cwbi/chainguard-base-fips:latest AS base
# The docs and AOT/trimming build packs these packages install are never used by the runtime;
# they are removed in the same layer because the release verifier forbids .md and .nupkg paths.
RUN apk update && apk add --no-cache aspnet-10-runtime libstdc++ icu-libs tzdata curl && \
    rm -rf /usr/share/doc /usr/share/dotnet/library-packs
WORKDIR /app
EXPOSE 8083

ENV ASPNETCORE_URLS=http://+:8083
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV PathBase=/total-risk

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
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

RUN adduser -D -g "" -h /app appuser && \
    chown -R appuser:appuser /app
USER appuser

HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl --fail --silent --show-error --output /dev/null http://localhost:8083/total-risk/health || exit 1

ENTRYPOINT ["dotnet", "RMC.TotalRisk.Api.dll"]
