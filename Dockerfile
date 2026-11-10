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
# Both font packages and their dependencies are in Ubuntu main. Avoid downloading
# universe/restricted/multiverse indexes just to install these small dependencies.
# Use HTTPS and bounded network waits rather than hanging on archive HTTP retries.
RUN set -eu; \
    for source in /etc/apt/sources.list.d/*.sources; do \
        [ -f "$source" ] || continue; \
        sed -i -e 's|http://|https://|g' -e 's/^Components:.*/Components: main/' "$source"; \
    done; \
    if [ -f /etc/apt/sources.list ]; then \
        sed -i 's|http://|https://|g' /etc/apt/sources.list; \
    fi; \
    apt-get -o Acquire::Languages=none -o Acquire::Retries=1 \
        -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 \
        -o APT::Update::Error-Mode=any update \
    && apt-get -o Acquire::Retries=1 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 \
        install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown app:app /data
COPY --from=build /out/ .
ENV TZ=Asia/Shanghai Monitor__DataDirectory=/data
USER $APP_UID
VOLUME ["/data"]
HEALTHCHECK --interval=1m --timeout=10s --start-period=90s --retries=3 CMD ["dotnet", "Smzdm.Monitor.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Smzdm.Monitor.dll"]
