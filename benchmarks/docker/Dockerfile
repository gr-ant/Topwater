# Arm C: real bash on Ubuntu 24.04 with the same tool set the benchmark relies on (jq, git, tar, coreutils) and two
# small clients, `shop` and `to-markdown`, that call the host application over a Unix socket. Episodes run it with
# no network, a read-only root, size-capped tmpfs for /tmp and /home/agent, and memory and process limits.
FROM ubuntu:24.04
# Image 2 (round 2, arms Cp and Bc) adds python3: docker build -t twbench-ubuntu:2 --build-arg EXTRA=python3 benchmarks/docker
ARG EXTRA=
RUN apt-get -qq update && DEBIAN_FRONTEND=noninteractive apt-get -qq install -y --no-install-recommends jq git curl file $EXTRA >/dev/null \
    && rm -rf /var/lib/apt/lists/* && userdel -r ubuntu && useradd -m -u 1000 -s /bin/bash agent
COPY shop to-markdown /usr/local/bin/
RUN chmod 755 /usr/local/bin/shop /usr/local/bin/to-markdown
USER agent
WORKDIR /home/agent
