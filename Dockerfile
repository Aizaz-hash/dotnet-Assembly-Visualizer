# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes)
COPY dotnet-Assembly-Visualizer.csproj .
RUN dotnet restore

# Copy the rest and publish
COPY . .
RUN dotnet publish dotnet-Assembly-Visualizer.csproj -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# aspnet:8.0 defaults to port 8080 and a non-root "app" user
EXPOSE 8080
# appsettings.json may set another Urls value (e.g. for the Windows service); the container always listens on 8080
ENV Urls=http://+:8080

# aspnet has no curl/wget, so probe /healthz with bash's /dev/tcp
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\r\n\r\n' >&3 && head -n1 <&3 | grep -q 200"
USER app

ENTRYPOINT ["dotnet", "dotnet-Assembly-Visualizer.dll"]
