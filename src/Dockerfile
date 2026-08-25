# ISEStudio .NET 10 backend.
#
# BUILD CONTEXT: ./src (the .NET solution root). docker-compose.yml and
# the shadow compose set `build.context` accordingly. The Dockerfile is
# co-located with the .NET solution it builds, so `COPY . ./` lands the
# whole `src/` tree in `/src/` and `dotnet publish OnToPilot/...` works
# without any path prefix gymnastics.
#
# This image is intentionally HEALTHCHECK-less: compose controls healthchecks
# (per migration invariant #8) so the contract path stays singular at /api/health.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# `dotnet publish` performs restore + publish in a single layer, which keeps
# `obj/project.assets.json` resolution consistent inside one RUN. Splitting
# restore into a separate layer with `--no-restore` here leaks host fallback
# paths (e.g. `d:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`)
# into `project.assets.json` that the publish step then can't resolve inside
# the Linux container. The single-step form is what the integration-test
# fixture (ContainerSmokeFixture.cs) relies on.
COPY . ./
RUN dotnet publish OnToPilot/OnToPilot.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl so the compose healthcheck
# (`curl -fsS http://127.0.0.1:8080/api/health`) works inside the runtime
# container — `mcr.microsoft.com/dotnet/aspnet:10.0` is based on
# `debian:bookworm-slim` which deliberately omits curl/ wget. ~200 KB.
# `--no-install-recommends` keeps the layer tiny; `rm -rf /var/lib/apt/lists/*`
# strips apt's cache so the layer doesn't carry it forward.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Persistent backend state (extraction jobs, RDF snapshots, etc.) lives under
# /app/data — declared as a VOLUME so docker-compose can mount a named volume
# at the same path without `VOLUME` colliding at runtime.
VOLUME ["/app/data"]

COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OnToPilot.dll"]