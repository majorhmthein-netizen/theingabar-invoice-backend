# Theingabar Invoice backend — C# / ASP.NET Core 8
# Multi-stage: SDK builds, ASP.NET runtime runs. Non-root (app user).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY TheingabarInvoice.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY public ./public
# The JSON fallback store writes ./data/ when DATABASE_URL is unset.
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 10000
ENTRYPOINT ["dotnet", "TheingabarInvoice.dll"]
