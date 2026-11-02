# Build environment for bdfu-vss.dll: mingw-w64 (>= 10 has vss.h, vsprov.h, vsadmin.h, comadmin.h).
FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends g++-mingw-w64-x86-64-win32 binutils-mingw-w64-x86-64 osslsigncode \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
