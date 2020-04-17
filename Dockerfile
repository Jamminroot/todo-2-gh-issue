FROM alpine:3.11

# Add some libs required by .NET runtime 
# https://github.com/dotnet/core/blob/master/Documentation/build-and-install-rhel6-prerequisites.md#troubleshooting
RUN apk add --no-cache \ 
    #openssh libunwind \
    #nghttp2-libs libidn krb5-libs libuuid lttng-ust zlib \
    libstdc++ libintl \
    icu

# Copy 
WORKDIR /app
COPY ./publish ./

ENTRYPOINT ["./TODO2WI"]