FROM alpine:3.11
RUN apk add --no-cache \ 
    #openssh libunwind \
    #nghttp2-libs libidn krb5-libs libuuid lttng-ust zlib \
    libstdc++ libintl \
    icu

# Copy 
WORKDIR /app
COPY ./publish ./

ENTRYPOINT ["./todo-2-gh-issue"]