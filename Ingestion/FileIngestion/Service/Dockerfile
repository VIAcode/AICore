FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
#COPY ["/Service/Service.csproj", "Service/"]
COPY ["/Ingestion/FileIngestion/Service/Service.csproj", "Service/"]
RUN dotnet restore "Service/Service.csproj"
COPY . .
WORKDIR "/src/Service"
RUN dotnet build "Service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Note: the following is borrowed from the original Kernel Memory repository

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS run

ARG USER=fis

WORKDIR /app

RUN \
    useradd --create-home --user-group $USER --shell /bin/bash && \
    # Ubuntu
    # adduser -D -h /app -s /bin/sh $USER && \
    chown -R $USER:$USER /app

COPY --from=publish --chown=$USER:$USER --chmod=0550 /app/publish .

USER $USER

EXPOSE 8080
ENTRYPOINT ["dotnet", "AiCore.FileIngestion.ServiceAssembly.dll"]
