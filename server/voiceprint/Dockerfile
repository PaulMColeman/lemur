# Build context: this folder (server/voiceprint).   docker build -t voiceprint .
# arm64 hosts: --build-arg RID=linux-arm64 (sherpa-onnx ships natives for both).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG RID=linux-x64
WORKDIR /src
COPY Directory.Build.props ./
COPY Voiceprint.Service/Voiceprint.Service.csproj Voiceprint.Service/
RUN dotnet restore Voiceprint.Service/Voiceprint.Service.csproj -r $RID
COPY Voiceprint.Service/ Voiceprint.Service/
RUN dotnet publish Voiceprint.Service/Voiceprint.Service.csproj -c Release -r $RID --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The model is not baked in: mount a folder at /models (read-only is enough).
# HTTP_PORTS is cleared so it doesn't compete with URLS (the base image sets it to 8080).
ENV ASPNETCORE_URLS=http://+:8090 \
    ASPNETCORE_HTTP_PORTS= \
    VOICEPRINT_MODEL_PATH=/models/model.onnx
EXPOSE 8090
USER $APP_UID
# No HEALTHCHECK: the aspnet image has neither curl nor wget. Probe GET /healthz from outside.
ENTRYPOINT ["dotnet", "Voiceprint.Service.dll"]
