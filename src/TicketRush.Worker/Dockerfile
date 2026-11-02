# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/TicketRush.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/TicketRush.Worker/TicketRush.Worker.csproj
RUN dotnet publish src/TicketRush.Worker/TicketRush.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "TicketRush.Worker.dll"]
