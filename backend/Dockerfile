FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY AeroSynapse.sln ./
COPY backend/src/AeroSynapse.API/AeroSynapse.API.csproj backend/src/AeroSynapse.API/
COPY backend/src/AeroSynapse.Application/AeroSynapse.Application.csproj backend/src/AeroSynapse.Application/
COPY backend/src/AeroSynapse.Domain/AeroSynapse.Domain.csproj backend/src/AeroSynapse.Domain/
COPY backend/src/AeroSynapse.Infrastructure/AeroSynapse.Infrastructure.csproj backend/src/AeroSynapse.Infrastructure/
RUN dotnet restore backend/src/AeroSynapse.API/AeroSynapse.API.csproj
COPY backend/src/ backend/src/
COPY contracts/ contracts/
RUN dotnet publish backend/src/AeroSynapse.API/AeroSynapse.API.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM build AS tooling
COPY .config/dotnet-tools.json .config/dotnet-tools.json
RUN dotnet tool restore
ENTRYPOINT ["dotnet", "ef"]

FROM build AS storage-tests
COPY backend/tests/ backend/tests/
RUN dotnet build backend/tests/AeroSynapse.Infrastructure.Tests/AeroSynapse.Infrastructure.Tests.csproj -c Release -o /tests
ENTRYPOINT ["dotnet", "/tests/AeroSynapse.Infrastructure.Tests.dll"]

FROM build AS contract-tests
COPY backend/tests/AeroSynapse.Contract.Tests/ backend/tests/AeroSynapse.Contract.Tests/
COPY contracts/ contracts/
RUN dotnet build backend/tests/AeroSynapse.Contract.Tests/AeroSynapse.Contract.Tests.csproj -c Release -o /contract-tests
ENTRYPOINT ["dotnet", "/contract-tests/AeroSynapse.Contract.Tests.dll"]

FROM build AS management-tests
COPY backend/tests/AeroSynapse.Management.Tests/ backend/tests/AeroSynapse.Management.Tests/
COPY backend/examples/ backend/examples/
RUN dotnet build backend/tests/AeroSynapse.Management.Tests/AeroSynapse.Management.Tests.csproj -c Release -o /management-tests
ENTRYPOINT ["dotnet", "/management-tests/AeroSynapse.Management.Tests.dll"]

FROM build AS critical-tests
COPY backend/tests/ backend/tests/
COPY backend/examples/ backend/examples/
COPY scripts/critical-dotnet.sh scripts/critical-dotnet.sh
RUN dotnet build backend/tests/AeroSynapse.Tests/AeroSynapse.Tests.csproj -m:1 -c Release --artifacts-path /critical-tests \
 && dotnet build backend/tests/AeroSynapse.Contract.Tests/AeroSynapse.Contract.Tests.csproj -m:1 -c Release --artifacts-path /critical-tests \
 && dotnet build backend/tests/AeroSynapse.Management.Tests/AeroSynapse.Management.Tests.csproj -m:1 -c Release --artifacts-path /critical-tests
ENTRYPOINT ["sh", "/src/scripts/critical-dotnet.sh"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN mkdir /data && chown "$APP_UID" /data
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ConnectionStrings__AirportMaps="Data Source=/data/airport-maps.db"
ENV ConnectionStrings__Identity="Data Source=/data/identity.db"
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AeroSynapse.API.dll"]
