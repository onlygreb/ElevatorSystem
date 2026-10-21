# syntax=docker/dockerfile:1

# The API and the engine for Docker Compose (ADR-0029), built with only Docker installed. Released images are still
# built by the .NET SDK (ADR-0011); both start from the same chiseled runtime: no shell, no package manager, non-root.
#
#   docker build -f deploy/Dockerfile --target api .
#   docker build -f deploy/Dockerfile --target engine .

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /src

# The sources are copied before restoring: the SDK adds implicit packages from what a project contains (its Razor and
# web assets), so a restore from project files alone would not match the lock files. The package cache is a mount.
COPY .editorconfig global.json NuGet.config Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/ElevatorSystem.Api/ElevatorSystem.Api.csproj --locked-mode \
    && dotnet restore src/ElevatorSystem.Engine/ElevatorSystem.Engine.csproj --locked-mode

FROM build AS publish-api
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/ElevatorSystem.Api/ElevatorSystem.Api.csproj --configuration Release --no-restore --output /app \
    && mkdir -p /https

FROM build AS publish-engine
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/ElevatorSystem.Engine/ElevatorSystem.Engine.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS api
WORKDIR /app
COPY --from=publish-api /app .
# Where compose keeps the HTTPS certificate; owned by the app user, so the API can make one on first start (ADR-0034).
COPY --from=publish-api --chown=$APP_UID:$APP_UID /https /https
USER $APP_UID
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "ElevatorSystem.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS engine
WORKDIR /app
COPY --from=publish-engine /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ElevatorSystem.Engine.dll"]
