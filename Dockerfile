# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files for restore
COPY DiscordSky.sln ./
COPY src/DiscordSky.Bot/DiscordSky.Bot.csproj src/DiscordSky.Bot/
COPY tests/DiscordSky.Tests/DiscordSky.Tests.csproj tests/DiscordSky.Tests/

RUN dotnet restore DiscordSky.sln

# Copy the remaining source and publish the bot
COPY . .
RUN dotnet publish src/DiscordSky.Bot/DiscordSky.Bot.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "DiscordSky.Bot.dll"]
