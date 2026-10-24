# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes)
COPY DotNetVisualizer.Web.csproj .
RUN dotnet restore

# Copy the rest and publish
COPY . .
RUN dotnet publish DotNetVisualizer.Web.csproj -c Release -o /app --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# aspnet:8.0 defaults to port 8080 and a non-root "app" user
EXPOSE 8080
# appsettings.json sets Urls to :9999 (Windows service); the container listens on 8080
ENV Urls=http://+:8080
USER app

ENTRYPOINT ["dotnet", "DotNetVisualizer.Web.dll"]
