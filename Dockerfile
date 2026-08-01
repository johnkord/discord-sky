# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the bot project file and restore. The runtime image needs only the bot, so we restore it
# directly rather than the whole solution (which also references the test and tools projects).
COPY src/DiscordSky.Bot/DiscordSky.Bot.csproj src/DiscordSky.Bot/
RUN dotnet restore src/DiscordSky.Bot/DiscordSky.Bot.csproj

# Copy the remaining source and publish the bot
COPY . .
RUN dotnet publish src/DiscordSky.Bot/DiscordSky.Bot.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
# scripts/deploy.sh --include-steward publishes a self-contained Linux bundle here. The tracked
# placeholder keeps ordinary Sky-only image builds working when autonomy is not packaged.
COPY artifacts/discord-steward/ /app/steward/

ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "DiscordSky.Bot.dll"]
