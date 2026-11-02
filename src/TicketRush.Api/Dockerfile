# syntax=docker/dockerfile:1
# Build from the repository root: docker build -f src/TicketRush.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet restore src/TicketRush.Api/TicketRush.Api.csproj
RUN dotnet publish src/TicketRush.Api/TicketRush.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "TicketRush.Api.dll"]
