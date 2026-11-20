# Build context is the repository root:
#   docker build -f deploy/Dockerfile -t ignixa-federation .
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Restore inputs first so the restore layer is cached until a project file or build config changes.
# .editorconfig is needed because EnforceCodeStyleInBuild reads it.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig Ignixa.Federation.slnx ./
COPY src/Ignixa.Federation.Core/Ignixa.Federation.Core.csproj src/Ignixa.Federation.Core/
COPY src/Ignixa.Federation.Shards/Ignixa.Federation.Shards.csproj src/Ignixa.Federation.Shards/
COPY src/Ignixa.Federation.Api/Ignixa.Federation.Api.csproj src/Ignixa.Federation.Api/
RUN dotnet restore src/Ignixa.Federation.Api/Ignixa.Federation.Api.csproj

COPY src/ src/
RUN dotnet publish src/Ignixa.Federation.Api/Ignixa.Federation.Api.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
LABEL org.opencontainers.image.title="ignixa-federation" \
      org.opencontainers.image.description="Read-only FHIR R4 search federation gateway over multiple FHIR servers" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.source="https://github.com/brendankowitz/fhir-federation-service"
WORKDIR /app
# MIT requires the license text to travel with copies of the software.
COPY LICENSE /app/LICENSE
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The official .NET images define a non-root user and expose its id as APP_UID; run as it, not root.
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Ignixa.Federation.Api.dll"]
