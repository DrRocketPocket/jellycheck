# Jellycheck

Jellycheck is a plugin for Jellyfin. It shows which users have watched a movie or TV show by displaying their user avatars on the posters in the web interface.

## Features

- Displays small user avatars on media posters.
- Supports custom user avatars (including GIFs).
- Generates letter-based avatars for users without a profile picture.
- Automatically injects the web interface code on startup and cleans it up when uninstalled.

## Installation

### Method 1: Plugin Repository (Recommended)
You can install the plugin directly through the Jellyfin dashboard:
1. Go to **Dashboard -> Plugins -> Repositories** on your Jellyfin server.
2. Add a new repository pointing to the URL of your hosted `manifest.json` file.
3. Install Jellycheck from the catalog and restart your server.

### Method 2: Manual Installation
1. Compile the plugin or download a pre-built version.
2. Place the `Jellycheck.dll` file inside your Jellyfin plugins directory (usually located under `plugins/Jellycheck/`).
3. Restart your Jellyfin server.

Future builds might contain an option to delete the Shows/Movies after everyone watched them.
