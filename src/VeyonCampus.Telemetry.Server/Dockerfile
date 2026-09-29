FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/VeyonCampus.Core/VeyonCampus.Core.csproj src/VeyonCampus.Core/
COPY src/VeyonCampus.Core/packages.lock.json src/VeyonCampus.Core/
COPY src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj src/VeyonCampus.Telemetry.Server/
COPY src/VeyonCampus.Telemetry.Server/packages.lock.json src/VeyonCampus.Telemetry.Server/
RUN dotnet restore src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj --locked-mode
COPY src/VeyonCampus.Telemetry.Server/ src/VeyonCampus.Telemetry.Server/
RUN dotnet publish src/VeyonCampus.Telemetry.Server/VeyonCampus.Telemetry.Server.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:9000
EXPOSE 9000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "VeyonCampus.Telemetry.Server.dll"]
