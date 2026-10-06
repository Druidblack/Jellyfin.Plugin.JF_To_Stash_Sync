# JF To Stash Sync
![logo](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/logo.jpg)
Jellyfin plugin for Synchronizing Jellyfin activity with Stash (For two-way synchronization, use [Jellyfin sync](https://druidblack.github.io/stash-plugin/main/index.yml) )

The video definition in stash will be based on the identifier that can be obtained using the plugin [Jellyfin.Plugin.Stash](https://github.com/DirtyRacer1337/Jellyfin.Plugin.Stash)


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

Synchronization of the playback position.

The plugin adds a provider for searching similar scenes.

![prov](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/prov.jpg)

The search button in stash. After clicking it, the metadata will be updated, and if necessary, the catalog in stash where the video is located will be rescanned.

![search](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/search%20stash.jpg)

We add gender icons to the actors’ portraits.

![gend](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/actor%20gender.jpg)

The playback panel now includes O‑counter buttons and a list of actors.

![cun](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/cun.jpg)

![fev](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/actorfev.jpg)

Social media icons (if the actor’s description includes links)

![soc](https://github.com/Druidblack/Jellyfin.Plugin.JF_To_Stash_Sync/blob/main/images/name.jpg)
