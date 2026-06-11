# Jellycheck - Jellyfin Watched Status Avatars Overlay

Jellycheck is a Jellyfin server plugin that displays small user avatars on media posters in the web interface. It shows you which users on the server have watched a movie, show, season, or episode.

## Features

- Displays avatars of users who have watched a media item on poster cards.
- Supports custom user profile images (including GIFs).
- Generates letter-based avatars for users without profile images.
- Automatically injects the client script into Jellyfin Web's index.html and removes it when uninstalled.
- Stops background page scanning when the browser tab is not active.

## Installation

### Method 1: Repository Catalog
You can host the manifest.json file to distribute the plugin.
1. In your Jellyfin dashboard, go to Dashboard -> Plugins -> Repositories.
2. Click Add and enter:
   - Repository Name: Jellycheck
   - Repository URL: https://raw.githubusercontent.com/<YOUR_GITHUB_USER>/jellycheck/main/manifest.json (replace with your GitHub details).
3. Save, go to the Catalog tab, find Jellycheck, and click Install.
4. Restart your Jellyfin server.

### Method 2: Manual Installation
1. Download or compile Jellycheck.dll.
2. Stop the Jellyfin server.
3. Place Jellycheck.dll in your Jellyfin data directory under plugins/Jellycheck/ (create the folders if they do not exist).
4. Restart the Jellyfin server.

## Manual Web Client Injection (Fallback)
If your Jellyfin server filesystem is read-only (such as in certain Docker setups) and the plugin cannot modify index.html, you can load the script manually using a custom JavaScript loader or browser extension.
Use this URL:
```
http://<YOUR_JELLYFIN_IP_AND_PORT>/jellycheck/client.js
```

## Development and Testing

### Prerequisites
- .NET 6.0 SDK (to build for Jellyfin 10.8)
- Docker and Docker Compose

### Local Test Instance Setup
The repository includes a pre-configured Docker testing setup for Jellyfin 10.8.13:
1. Open PowerShell in the project directory.
2. Run the test script:
   ```powershell
   ./run-test.ps1
   ```
3. This compiles the assembly in Release mode, copies the DLL into the plugins directory, and starts the container.
4. Open http://localhost:8096 in your browser.

## Build and Release Instructions

To build the plugin DLL:
```bash
dotnet build -c Release
```

To release a new version:
1. Zip the compiled Jellycheck.dll file.
2. Compute the MD5 hash checksum of the zip file.
3. Upload the zip file to GitHub Releases or your hosting provider.
4. Update manifest.json with the new version, download URL, MD5 checksum, and release timestamp.
