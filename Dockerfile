FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/FCG.Notifications/FCG.Notifications.csproj src/FCG.Notifications/packages.lock.json src/FCG.Notifications/
RUN dotnet restore src/FCG.Notifications/FCG.Notifications.csproj --locked-mode
COPY src/FCG.Notifications/ src/FCG.Notifications/
RUN dotnet publish src/FCG.Notifications/FCG.Notifications.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:011bb5f30180717b1c8b65822ff2c99bcb96bc65af0164589751b83c7b4949f7 AS runtime
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
HEALTHCHECK --interval=3s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "FCG.Notifications.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "FCG.Notifications.dll"]
