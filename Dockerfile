# Headless FatGuysSpeak server image (for Azure Container Apps, a VM, or any container host).
# The project multi-targets net9.0 and net9.0-windows10.0.19041.0; on Linux we build ONLY the
# net9.0 target with -f net9.0 (the Windows/WPF dashboard target can't build here, and isn't
# needed for hosting). App Service for Linux does NOT need this file — it deploys the published
# app directly via the GitHub Actions workflow.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY FatGuysSpeak.Shared/ FatGuysSpeak.Shared/
COPY FatGuysSpeak.Server/ FatGuysSpeak.Server/
# EnableWindowsTargeting lets the Linux SDK resolve the net9.0-windows target's references during
# restore without building it (we build only net9.0); without it the SDK errors NETSDK1100.
RUN dotnet publish FatGuysSpeak.Server/FatGuysSpeak.Server.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Container hosts route to this port; Azure Container Apps expects the app on its target port.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "FatGuysSpeak.Server.dll"]
