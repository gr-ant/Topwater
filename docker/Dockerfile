# The topwater command in a container. The build context is a folder holding the release's standalone binaries as
# linux-amd64/topwater and linux-arm64/topwater; nothing is compiled or run here, so both architectures build on
# any machine without emulation. The release workflow (job docker) and CI (job docker) show how to fill it.
#
# Ubuntu's chiseled .NET base: no shell or package manager, a non-root user (app, uid 1654), and exactly what a
# self-contained .NET binary loads (glibc, libstdc++, zlib, OpenSSL, CA certificates). It has no ICU, so .NET runs
# with invariant globalization, which Topwater's test suite passes under.
# Refresh the digest: docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:2bb1d484500b032aaafb75e876ffbea135b969d6259cacfcbf18a793e5d6e6ec

ARG TARGETARCH
ARG VERSION=dev
ARG REVISION=unknown

LABEL org.opencontainers.image.title="topwater" \
      org.opencontainers.image.description="A simulated bash shell for AI agents: an interactive shell, one-shot runs and an MCP server with a sandboxed bash tool" \
      org.opencontainers.image.source="https://github.com/gr-ant/Topwater" \
      org.opencontainers.image.url="https://github.com/gr-ant/Topwater/blob/main/docs/cli.md#docker" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"

COPY linux-${TARGETARCH}/topwater /usr/local/bin/topwater

# /data, owned by the image's user, for --state files: relative paths land here, and a named volume starts writable
USER app
WORKDIR /data
VOLUME /data

ENTRYPOINT ["topwater"]
CMD ["shell"]
