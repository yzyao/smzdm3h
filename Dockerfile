FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Smzdm.Monitor/Smzdm.Monitor.csproj src/Smzdm.Monitor/
RUN dotnet restore src/Smzdm.Monitor/Smzdm.Monitor.csproj
COPY src/Smzdm.Monitor/ src/Smzdm.Monitor/
RUN dotnet publish src/Smzdm.Monitor/Smzdm.Monitor.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
COPY --from=build /out/ .
ENV TZ=Asia/Shanghai Monitor__DataDirectory=/data
USER $APP_UID
VOLUME ["/data"]
HEALTHCHECK --interval=1m --timeout=10s --start-period=90s --retries=3 CMD ["dotnet", "Smzdm.Monitor.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Smzdm.Monitor.dll"]
