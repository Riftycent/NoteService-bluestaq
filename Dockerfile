# Build context is the repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached until a project file changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY NoteService.API/NoteService.API.csproj NoteService.API/
COPY NoteService.Domain/NoteService.Domain.csproj NoteService.Domain/
COPY NoteService.Infrastructure/NoteService.Infrastructure.csproj NoteService.Infrastructure/
RUN dotnet restore NoteService.API/NoteService.API.csproj

COPY NoteService.API/ NoteService.API/
COPY NoteService.Domain/ NoteService.Domain/
COPY NoteService.Infrastructure/ NoteService.Infrastructure/
RUN dotnet publish NoteService.API/NoteService.API.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Attachments live on a volume the non-root user can write to.
ENV ASPNETCORE_URLS=http://+:8080 \
    BlobStorage__RootPath=/data/blobs
RUN mkdir -p /data/blobs && chown -R $APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "NoteService.API.dll"]
