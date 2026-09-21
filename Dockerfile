FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config .
COPY AiPulse.csproj .
RUN dotnet restore AiPulse.csproj
COPY . .
RUN dotnet publish AiPulse.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# App_Data holds the SQLite DB and per-user state - must be a mounted volume, not
# container-writable-layer storage, or a redeploy silently wipes every user/bookmark/setting.
VOLUME ["/app/App_Data"]

# .NET 8's Linux container images default to a non-root "app" user and port 8080 already -
# set explicitly so behaviour doesn't depend on the base image's current default.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "AiPulse.dll"]
