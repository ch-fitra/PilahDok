FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY libs/PilahDok.Core/PilahDok.Core.csproj libs/PilahDok.Core/
COPY libs/PilahDok.Infrastructure/PilahDok.Infrastructure.csproj libs/PilahDok.Infrastructure/
COPY apps/worker/PilahDok.Worker.csproj apps/worker/
RUN dotnet restore apps/worker/PilahDok.Worker.csproj
COPY libs/ libs/
COPY apps/worker/ apps/worker/
RUN dotnet publish apps/worker/PilahDok.Worker.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/pilahdok/jobs \
    && chown -R 1654:1654 /data/pilahdok
WORKDIR /app
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
ENV ASPNETCORE_URLS=http://+:8081 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8081
ENTRYPOINT ["dotnet", "PilahDok.Worker.dll"]
