# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so source edits do not invalidate the restore layer.
COPY Directory.Build.props ./
COPY src/Rc.Protocol/Rc.Protocol.csproj src/Rc.Protocol/
COPY src/Rc.Relay/Rc.Relay.csproj src/Rc.Relay/
RUN dotnet restore src/Rc.Relay/Rc.Relay.csproj

COPY src/ src/
RUN dotnet publish src/Rc.Relay/Rc.Relay.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "rcrelay.dll"]
