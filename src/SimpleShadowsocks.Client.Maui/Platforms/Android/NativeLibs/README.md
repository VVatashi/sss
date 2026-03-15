Place prebuilt `hev-socks5-tunnel` binaries here as native libraries:

- `arm64-v8a/libhev-socks5-tunnel.so` for physical ARM64 devices.
- `x86_64/libhev-socks5-tunnel.so` for Android emulator x86_64 images.

The Android client loads the native library in-process. Do not place it into app files directory and try to execute it as a standalone binary.
