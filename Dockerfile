# Builds only the server. The pipeline, tests and layer data never enter the image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, on the project files alone, so source edits do not redo it.
COPY src/CoaMcp.Core/CoaMcp.Core.csproj src/CoaMcp.Core/
COPY src/CoaMcp.Server/CoaMcp.Server.csproj src/CoaMcp.Server/
RUN dotnet restore src/CoaMcp.Server/CoaMcp.Server.csproj
COPY src/CoaMcp.Core src/CoaMcp.Core
COPY src/CoaMcp.Server src/CoaMcp.Server
RUN dotnet publish src/CoaMcp.Server/CoaMcp.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# A fixed UID so file ownership on the host does not depend on the image build.
# /data is the host volume. Layer files and current.json are root-owned and not writable by this user; only /data/writable is.
RUN groupadd --gid 10001 coamcp && useradd --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin coamcp
WORKDIR /app
COPY --from=build /app .
COPY scripts/backup-store.sh /app/backup-store.sh
USER 10001:10001
# The base image also sets ASPNETCORE_HTTP_PORTS=8080; clearing it avoids a startup warning about two port settings.
ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_URLS=http://+:8080 \
    Layers__Root=/data
EXPOSE 8080
# The aspnet image has no curl or wget, so the check opens a TCP connection with bash and reads the status line.
# Loading and verifying the layers happens before the server listens, hence the long start period.
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "CoaMcp.Server.dll"]
