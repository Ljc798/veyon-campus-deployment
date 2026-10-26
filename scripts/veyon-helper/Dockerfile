FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive
ARG VEYON_VERSION=4.11.4
ARG VEYON_SOURCE_SHA256=92701c1ca8f794a738c6cd8ecec4b2a85bb63e2906e369df9c2d0ee97a1f1ce8
ARG BUILD_JOBS=2

ENV QT_QPA_PLATFORM=offscreen

RUN apt-get -o Acquire::Retries=5 update \
    && apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
        bzip2 \
        build-essential \
        ca-certificates \
        cmake \
        curl \
        dpkg-dev \
        libfakekey-dev \
        libjpeg-dev \
        libldap2-dev \
        liblzo2-dev \
        libpam0g-dev \
        libproc2-dev \
        libqca-qt6-dev \
        libsasl2-dev \
        libssl-dev \
        libvncserver-dev \
        libxtst-dev \
        python3 \
        qt6-5compat-dev \
        qt6-base-dev \
        qt6-base-private-dev \
        xorg-dev \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Veyon's config CLI requires the QCA OpenSSL plugin at runtime for RSA key operations.
RUN apt-get -o Acquire::Retries=5 update \
    && apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
        libqca-qt6-plugins \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /tmp
RUN curl --fail --location --retry 3 \
        "https://github.com/veyon/veyon/releases/download/v${VEYON_VERSION}/veyon-${VEYON_VERSION}-src.tar.bz2" \
        --output veyon-src.tar.bz2 \
    && echo "${VEYON_SOURCE_SHA256}  veyon-src.tar.bz2" | sha256sum --check --status \
    && mkdir -p /opt \
    && tar -xjf veyon-src.tar.bz2 -C /opt \
    && cmake -S "/opt/veyon-${VEYON_VERSION}" -B /tmp/veyon-build \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr/local \
        -DWITH_QT6=ON \
        -DWITH_TRANSLATIONS=OFF \
        -DWITH_TESTS=OFF \
        -DWITH_LTO=OFF \
        -DWITH_WEBAPI=OFF \
        -DCMAKE_C_FLAGS=-Wno-error=maybe-uninitialized \
        -DCMAKE_CXX_FLAGS=-Wno-error=maybe-uninitialized \
    && cmake --build /tmp/veyon-build --parallel "${BUILD_JOBS}" \
    && cmake --install /tmp/veyon-build \
    && rm -rf /opt/veyon-"${VEYON_VERSION}" /tmp/veyon-build /tmp/veyon-src.tar.bz2

COPY capture.py /usr/local/lib/veyon-helper/capture.py
RUN chmod 0555 /usr/local/lib/veyon-helper/capture.py

LABEL org.opencontainers.image.title="Veyon Campus ARM64 helper" \
      org.opencontainers.image.version="${VEYON_VERSION}" \
      org.opencontainers.image.source="https://github.com/veyon/veyon"

ENTRYPOINT ["/usr/local/bin/veyon-cli"]
CMD ["--version"]
