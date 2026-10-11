# One Dockerfile for every deployable: docker build --build-arg PROJECT=src/Services/Identity/Erp.Identity.Api --build-arg DLL=Erp.Identity.Api.dll .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG PROJECT
RUN dotnet publish $PROJECT -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ARG DLL
ENV APP_DLL=$DLL
USER $APP_UID
ENTRYPOINT ["sh", "-c", "dotnet $APP_DLL"]
