# Build one image per title from the repository root, e.g.:
#   docker build -f docker/Dockerfile --build-arg TITLE=Pes2011Pc -t openeleven-pes2011pc .
# TITLE is one of Pes2010Pc | Pes2011Pc | Pes2012Pc | Pes2013Pc.
# Ports are published per title in docker-compose.yml.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TITLE=Pes2010Pc
WORKDIR /src

COPY Directory.Build.props ./
COPY src/OpenEleven.Protocol/OpenEleven.Protocol.csproj src/OpenEleven.Protocol/
COPY src/OpenEleven.Data/OpenEleven.Data.csproj src/OpenEleven.Data/
COPY src/OpenEleven.Server/OpenEleven.Server.csproj src/OpenEleven.Server/
COPY src/OpenEleven.Profiles/${TITLE}/OpenEleven.Profiles.${TITLE}.csproj src/OpenEleven.Profiles/${TITLE}/
COPY src/OpenEleven.${TITLE}/OpenEleven.${TITLE}.csproj src/OpenEleven.${TITLE}/
RUN dotnet restore "src/OpenEleven.${TITLE}/OpenEleven.${TITLE}.csproj"

COPY src/ src/
COPY conf/ conf/
RUN dotnet publish "src/OpenEleven.${TITLE}/OpenEleven.${TITLE}.csproj" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
ARG TITLE=Pes2010Pc
WORKDIR /app

COPY --from=build /app ./

RUN mkdir -p /app/data /app/logs

ENV TITLE=${TITLE}
ENTRYPOINT dotnet "OpenEleven.${TITLE}.dll" --config conf/server.yaml
