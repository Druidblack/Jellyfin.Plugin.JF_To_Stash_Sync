# JF To Stash Sync
![logo](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/logo.jpg)
Sync Jellyfin activity to Stash: increments play count for each play session and reports the real watched time, Performer favorites, Favorite videos → Stash rating

The video definition in stash will be based on the identifier that can be obtained using the plugin [Jellyfin.Plugin.Stash](https://github.com/DirtyRacer1337/Jellyfin.Plugin.Stash)

If you don't have a video ID, the plugin can match the name of the video file or the full path (for reliability)

# Installation
1. Add the following manifest URL to your Jellyfin **Plugin Repositories**:
```
https://raw.githubusercontent.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/main/manifest.json
```
2. Navigate to the Catalog and refresh the page.
3. Locate and install JF To Stash Sync.
4. Restart your Jellyfin server.


Every video view in jellyfin will send data here:

![info](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/info.jpg)

Synchronization Performer favorites

![actor](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/actor.jpg)

Synchronization Favorite videos → Stash rating

![rating](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/rating.jpg)
