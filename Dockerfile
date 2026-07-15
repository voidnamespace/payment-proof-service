FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/PaymentProof.Api/PaymentProof.Api.csproj", "src/PaymentProof.Api/"]
RUN dotnet restore "src/PaymentProof.Api/PaymentProof.Api.csproj"

COPY . .
WORKDIR "/src/src/PaymentProof.Api"
RUN dotnet publish "PaymentProof.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "PaymentProof.Api.dll"]
