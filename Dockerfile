# Theingabar Invoice backend — C# / ASP.NET Core 8 (HTML-only version)
# Multi-stage: SDK builds, ASP.NET runtime runs. Non-root (app user).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY TheingabarInvoice.csproj .
RUN dotnet restore
COPY *.cs ./
COPY Pages ./Pages
COPY Services ./Services
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# wwwroot: new HTML-only frontend assets (css, fonts incl. PDF TTFs, js, sw.js).
COPY wwwroot ./wwwroot
# public: legacy PWA, served under /legacy/ until cutover.
COPY public ./public
# The JSON fallback store writes ./data/ when DATABASE_URL is unset;
# data-protection keys also persist under ./data/protection-keys.
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 10000
ENTRYPOINT ["dotnet", "TheingabarInvoice.dll"]
