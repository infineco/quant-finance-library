# Linux environment for the opt-in CUDA generation (build/hybridizer/Hybridizer.targets) and the GPU tests.
# Verified on Ubuntu 24.04 host, NVIDIA driver 595, RTX 3060 Ti; see docs/verification-linux-cuda.md.
#
#   docker build -t qfl-cuda build/hybridizer/linux
#   docker run --rm --gpus all -v "$PWD":/work -w /work qfl-cuda bash -lc \
#     "dotnet build InFine.QuantFinance.Cuda/InFine.QuantFinance.Cuda.csproj -c Release -p:HybridizeCuda=true \
#      && dotnet build QuantFinance.sln -c Release && dotnet test QuantFinance.sln -c Release --no-build"
#
# The device code is JIT-compiled by nvrtc for the GPU visible at build time, so build and run on the same GPU model.
FROM nvidia/cuda:13.0.0-devel-ubuntu24.04

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends curl ca-certificates libicu74 git \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_ROOT=/root/.dotnet \
    PATH=/root/.dotnet:/root/.dotnet/tools:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /root/.dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet tool install -g Hybridizer --version 7.6.3
