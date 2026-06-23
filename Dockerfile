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

COPY --from=build /app/publish .

ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "DiscordSky.Bot.dll"]
