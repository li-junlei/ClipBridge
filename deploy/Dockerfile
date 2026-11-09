FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ClipBridge.Core/ src/ClipBridge.Core/
COPY src/ClipBridge.Relay/ src/ClipBridge.Relay/
RUN dotnet publish src/ClipBridge.Relay -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 CLIPBRIDGE_RELAY_CONFIG=/config/relay.json CLIPBRIDGE_DATA=/data
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "ClipBridge.Relay.dll"]
