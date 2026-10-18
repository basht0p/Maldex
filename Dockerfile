# syntax=docker/dockerfile:1
#
# Production image for Maldex. Two final targets:
#   web      the ASP.NET host (default target)
#   migrate  a one-shot EF Core migrations bundle, run before web starts
#
# Packages only what the app has today. Add stages (UI build, YARA, etc.) as
# those features land. Used by deploy/docker-compose.yml; see DEPLOYMENT.md.

ARG DOTNET_VERSION=10.0

# --- build --------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY . .
RUN --mount=type=cache,id=maldex-nuget,target=/root/.nuget/packages \
    dotnet tool restore \
 && dotnet publish src/Maldex.Web/Maldex.Web.csproj -c Release -o /out/web \
 && dotnet ef migrations bundle --project src/Maldex.Data --configuration Release -o /out/efbundle

# --- migrate ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS migrate
WORKDIR /app
COPY --from=build /out/efbundle ./efbundle
USER app
# The connection string is passed at run time (see docker-compose.yml); never baked in.
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$MALDEX_MIGRATE_CONNSTR\""]

# --- web ----------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS web
# Artifact store mount point (Maldex:Artifacts:Root), owned by the image's non-root "app" user.
RUN mkdir -p /var/lib/maldex/artifacts && chown -R app:app /var/lib/maldex
WORKDIR /app
COPY --from=build /out/web ./
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Maldex__Artifacts__Root=/var/lib/maldex/artifacts
EXPOSE 8080
VOLUME ["/var/lib/maldex/artifacts"]
ENTRYPOINT ["dotnet", "Maldex.Web.dll"]
