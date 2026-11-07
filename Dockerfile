# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes)
COPY dotnet-Assembly-Visualizer.csproj .
COPY src/DotNetVisualizer.Core/DotNetVisualizer.Core.csproj src/DotNetVisualizer.Core/
RUN dotnet restore

# Copy the rest and publish
COPY . .
RUN dotnet publish dotnet-Assembly-Visualizer.csproj -c Release -o /app --no-restore

# The Dav PowerShell module (docs/dav-module.md) ...
RUN dotnet publish src/Dav/Dav.csproj -c Release -o /dav \
 && cp src/Dav/Dav.psd1 /dav/ \
 && rm -f /dav/*.pdb /dav/*.deps.json

# ... and PowerShell itself: the dotnet tool is framework-dependent, so it runs on the .NET 8 runtime that is already in the image
# (PowerShell 7.4 is the release line for .NET 8)
RUN dotnet tool install PowerShell --version "7.4.*" --tool-path /opt/pwsh

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# PowerShell with the Dav module, for the command-line checks:
#   docker run --rm -v "$PWD:/work" --entrypoint pwsh <image> -NoProfile -Command "Invoke-DavCheck /work/MyApp.dll -FailOn high"
# The module sits in a folder PowerShell searches on Linux, so Import-Module Dav (or just calling a cmdlet) finds it.
COPY --from=build /opt/pwsh /opt/pwsh
COPY --from=build /dav /usr/local/share/powershell/Modules/Dav
RUN ln -s /opt/pwsh/pwsh /usr/local/bin/pwsh
ENV POWERSHELL_TELEMETRY_OPTOUT=1 \
    POWERSHELL_UPDATECHECK=Off

# aspnet:8.0 defaults to port 8080 and a non-root "app" user
EXPOSE 8080
# appsettings.json may set another Urls value (e.g. for the Windows service); the container always listens on 8080
ENV Urls=http://+:8080

# aspnet has no curl/wget, so probe /healthz with bash's /dev/tcp
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\r\n\r\n' >&3 && head -n1 <&3 | grep -q 200"
USER app

ENTRYPOINT ["dotnet", "dotnet-Assembly-Visualizer.dll"]
