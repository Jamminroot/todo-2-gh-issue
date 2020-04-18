ARG REPO=mcr.microsoft.com/dotnet/core/runtime-deps
FROM $REPO:3.1-alpine3.11

ENV \
    ASPNETCORE_URLS= \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    LC_ALL=en_US.UTF-8 \
    LANG=en_US.UTF-8 \
    NUGET_XMLDOC_MODE=skip \
    POWERSHELL_DISTRIBUTION_CHANNEL=PSDocker-DotnetCoreSDK-Alpine-3.10

RUN apk add --no-cache icu-libs

RUN dotnet_sdk_version=3.1.201 \
    && wget -O dotnet.tar.gz https://dotnetcli.azureedge.net/dotnet/Sdk/$dotnet_sdk_version/dotnet-sdk-$dotnet_sdk_version-linux-musl-x64.tar.gz \
    && dotnet_sha512='9a8f14be881cacb29452300f39ee66f24e253e2df947f388ad2157114cd3f44eeeb88fae4e3dd1f9687ce47f27d43f2805f9f54694b8523dc9f998b59ae79996' \
    && echo "$dotnet_sha512  dotnet.tar.gz" | sha512sum -c - \
    && mkdir -p /usr/share/dotnet \
    && tar -C /usr/share/dotnet -oxzf dotnet.tar.gz \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm dotnet.tar.gz \
    && dotnet help

WORKDIR /app
COPY *.csproj ./
RUN dotnet restore
COPY *.cs ./
RUN dotnet build -c Release -o out

RUN ls -R
ENTRYPOINT ["./out/todo-2-gh-issue"]