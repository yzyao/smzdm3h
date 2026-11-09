FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /source
COPY src/Smzdm.Monitor/Smzdm.Monitor.csproj src/Smzdm.Monitor/
RUN dotnet restore src/Smzdm.Monitor/Smzdm.Monitor.csproj -a "$TARGETARCH" /p:UseAppHost=false /p:SelfContained=false
COPY src/Smzdm.Monitor/ src/Smzdm.Monitor/
# Publish for the target Linux architecture so other platforms' native libraries
# and their debugging symbols do not become part of the runtime image.
RUN dotnet publish src/Smzdm.Monitor/Smzdm.Monitor.csproj -c Release -a "$TARGETARCH" --self-contained false \
    -o /out --no-restore /p:UseAppHost=false /p:DebugType=None /p:DebugSymbols=false

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
