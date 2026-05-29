# Verifies generated shell-completion scripts load and complete correctly in
# the shells we can't easily run in `dotnet test`. Generated scripts are
# mounted read-only at /completions (see `just test-completions`).
ARG NU_VER=0.113.0
ARG ELV_VER=0.21.0
ARG OILS_VER=0.37.0
# Host arch by default; override for amd64 CI: --build-arg ARCH=x86_64 --build-arg ELVARCH=amd64
ARG ARCH=aarch64
ARG ELVARCH=arm64

# --- murex (Go build) ---
FROM golang:1.24-bookworm AS murexbuild
RUN go install github.com/lmorg/murex@latest

# --- oils / osh (build from release tarball; verifies the bash emitter under OSH) ---
FROM debian:bookworm-slim AS oilsbuild
ARG OILS_VER
RUN apt-get update && apt-get install -y --no-install-recommends \
      build-essential libreadline-dev curl ca-certificates xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL "https://oils.pub/download/oils-for-unix-${OILS_VER}.tar.gz" -o /tmp/oils.tar.gz \
    && cd /tmp && tar xf oils.tar.gz && cd "oils-for-unix-${OILS_VER}" \
    && ./configure && _build/oils.sh && ./install

# --- test image ---
FROM debian:bookworm-slim
ARG NU_VER
ARG ELV_VER
ARG ARCH
ARG ELVARCH
RUN apt-get update && apt-get install -y --no-install-recommends \
      bash zsh fish yash curl ca-certificates xz-utils tar python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*

# Nushell + Elvish (static release binaries)
RUN curl -fsSL "https://github.com/nushell/nushell/releases/download/${NU_VER}/nu-${NU_VER}-${ARCH}-unknown-linux-gnu.tar.gz" -o /tmp/nu.tgz \
    && tar -xzf /tmp/nu.tgz -C /tmp \
    && cp "/tmp/nu-${NU_VER}-${ARCH}-unknown-linux-gnu/nu" /usr/local/bin/nu
RUN curl -fsSL "https://dl.elv.sh/linux-${ELVARCH}/elvish-v${ELV_VER}.tar.gz" -o /tmp/elv.tgz \
    && mkdir -p /tmp/elv && tar -xzf /tmp/elv.tgz -C /tmp/elv \
    && mv "$(find /tmp/elv -type f -name 'elvish*' | head -1)" /usr/local/bin/elvish \
    && chmod +x /usr/local/bin/elvish
RUN pip install --break-system-packages --no-cache-dir xonsh

COPY --from=murexbuild /go/bin/murex /usr/local/bin/murex
COPY --from=oilsbuild  /usr/local/bin/oils-for-unix /usr/local/bin/oils-for-unix
RUN ln -s /usr/local/bin/oils-for-unix /usr/local/bin/osh

COPY run-tests.sh bash_probe.sh osh_probe.sh xonsh_probe.xsh elv_probe.py yash_probe.py /
CMD ["bash", "/run-tests.sh"]
