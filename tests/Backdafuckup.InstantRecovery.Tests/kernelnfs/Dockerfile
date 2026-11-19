# The Linux kernel's NFS client (mount.nfs from nfs-common) and fio, to test the server like ESXi's client would use it.
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends nfs-common netbase fio coreutils procps && rm -rf /var/lib/apt/lists/*
