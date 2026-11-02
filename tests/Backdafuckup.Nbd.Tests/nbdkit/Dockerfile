# nbdkit (BSD-licensed reference NBD server) for interoperability tests of our NBD client.
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends nbdkit \
 && rm -rf /var/lib/apt/lists/*
EXPOSE 10809
ENTRYPOINT ["nbdkit", "--foreground", "--verbose"]
