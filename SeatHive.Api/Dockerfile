# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY SeatHive.Shared/SeatHive.Shared.csproj SeatHive.Shared/
COPY SeatHive.Api/SeatHive.Api.csproj SeatHive.Api/
RUN dotnet restore SeatHive.Api/SeatHive.Api.csproj

COPY SeatHive.Shared/ SeatHive.Shared/
COPY SeatHive.Api/ SeatHive.Api/
RUN dotnet publish SeatHive.Api/SeatHive.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user that ships with the .NET images.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SeatHive.Api.dll"]
