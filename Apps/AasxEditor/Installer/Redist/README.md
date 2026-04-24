# Redist — bundled installers (optional)

Place either of the following files here **before** running `build-installer.bat` to make the resulting installer fully offline-capable. If you skip this folder entirely, the installer still works, but on machines that lack the WebView2 runtime it will require internet access to fetch the bootstrapper.

## WebView2 Runtime

Either of these files will be picked up automatically. If both are present, the offline standalone is preferred.

| File | Source | Size | Mode |
|---|---|---|---|
| `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` | [Evergreen Standalone Installer (x64)](https://developer.microsoft.com/microsoft-edge/webview2/) | ~170 MB | **Fully offline install** |
| `MicrosoftEdgeWebview2Setup.exe` | [Evergreen Bootstrapper](https://developer.microsoft.com/microsoft-edge/webview2/) | ~2 MB | Downloads at install time |

### Background

- Most Windows 10/11 systems have the WebView2 runtime preinstalled. The installer detects this via registry and skips the bundled redist on those machines.
- For air-gapped / restricted environments, ship the standalone (`...InstallerX64.exe`).
- This folder is `.gitignore`d to keep the repo small — the binaries are downloaded on demand by whoever builds the installer.
