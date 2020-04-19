FROM mcr.microsoft.com/dotnet/core/sdk:3.1
WORKDIR /app
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o out
ENTRYPOINT ["dotnet", "/app/out/todo-2-gh-issue.dll"]