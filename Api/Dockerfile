# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG GITHUB_USERNAME=AlfaHackathon
COPY NuGet.config ./
COPY Api/InsightService.Api.csproj Api/
# Credentials exist only for this restore process, never in build args or NuGet.config.
RUN --mount=type=secret,id=github_token,required=true \
    NuGetPackageSourceCredentials_github="Username=${GITHUB_USERNAME};Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" \
    dotnet restore Api/InsightService.Api.csproj --configfile NuGet.config
COPY Api/ Api/
RUN dotnet publish Api/InsightService.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
# Both APIs apply durable database migrations before opening their HTTP listener.
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "InsightService.Api.dll"]
