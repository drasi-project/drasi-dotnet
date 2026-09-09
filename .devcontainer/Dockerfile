# Shared tutorial image. Do not use mcr.microsoft.com/devcontainers/dotnet:
# that image ships a Yarn APT source whose GPG key is expired, so
# docker-in-docker's apt-get update fails (NO_PUBKEY 62D54FD4003F6525).
FROM mcr.microsoft.com/devcontainers/base:bookworm

RUN apt-get update \
    && export DEBIAN_FRONTEND=noninteractive \
    && apt-get -y install --no-install-recommends \
        cmake \
        clang \
        pkg-config \
        libssl-dev \
        zlib1g-dev \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
