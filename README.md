# AppLookup for ArchiSteamFarm

Look up a Steam app's ID, name and marketable status from chat.

```
applookup <appID|name>
look <appID|name>
```

(`al` is not used: it is ASF's built-in alias for `addlicense`.)

Examples (use your `CommandPrefix`, `!` by default):

```
!applookup 636270
!look Arcadia Cafe
```

Sample reply:

```
636270 | Arcadia Cafe | Marketable
```

Name searches are case-insensitive: exact matches first, then partial matches, up to 3 results.
Requires `Operator` access or higher.

To re-download the list immediately (clearing the cache), use a command name plus `refresh`,
e.g. `!lookrefresh` or `!applookuprefresh`. It needs `Master` access, has a 30 second cooldown, and keeps
the previous list if the download fails.

## Data source

By default AppLookup downloads the list from
`https://www.steamcardexchange.net/api/request.php?GetInventory`, whose rows look like:

```json
[[603780, "- Arcane preRaise -", 0, 1, 0], 6, 0, [5, 0, 0]]
```

The app ID, name and marketable flag are `row[0][0]`, `row[0][1]` and `row[0][4]`
(`1` = marketable, `0` = non-marketable).

A simpler layout from a custom API is also accepted, with or without a `{ "data": ... }` wrapper:

```json
{ "data": [ [636270, "Arcadia Cafe", 1], [789012, "Another Game", 0] ] }
```

There the flag is `row[2]`. The list is cached in memory for 30 minutes (a stale copy is kept if a refresh fails).
The response may be UTF-8 or UTF-16.

## Configuration

On first start the plugin creates `AppLookup.json` next to `AppLookup.dll` with defaults.
Edit it and restart ASF to change settings. See `AppLookup.example.json`.

| Setting | Default | Meaning |
|---|---|---|
| `ApiUrl` | steamcardexchange `GetInventory` link | Where the list is downloaded from (http/https). Empty = default. |
| `CacheMinutes` | 30 | How long the downloaded list is reused before refetching. |
| `UseDiskCache` | `true` | Also save the downloaded list to `AppLookup.cache` next to the plugin, so a restart within `CacheMinutes` skips the download (and an older copy is used if the API is down). Set `false` for read-only folders. |
| `UseMemoryCache` | `false` | `false` = disk only: the parsed list is dropped after every command and re-read from `AppLookup.cache` on the next one (low memory, slightly slower lookups). `true` keeps the parsed list in memory. If both caches are off, the disk cache is forced on. |
| `MaxResults` | 3 | Max matches shown for name searches (1-20). |
| `Aliases` | `applookup`, `look` | Command names, case-insensitive. |
| `MinimumAccess` | `Operator` | Lowest ASF access level allowed (e.g. `Master`, `Operator`, `FamilySharing`). |

The `APPLOOKUP_API_URL` environment variable, if set, overrides `ApiUrl`.
An invalid file is logged and defaults are used. Ship/update the plugin without overwriting
your `AppLookup.json` (only `AppLookup.example.json` is meant to be replaced).

## Build

1. Edit `AppLookup.csproj` so it references ASF (source project or `ArchiSteamFarm.dll`).
2. `dotnet publish -c Release -o out`
3. Copy `out/` into `ASF/plugins/AppLookup/` and restart ASF (generic build recommended).

## If it doesn't compile

- `EAccess` namespace: search the ASF source for `enum EAccess` and fix the `using`.
- `ArchiSteamFarm.Core.ASF.ArchiLogger`: adjust if the logger moved.
- `bot.ArchiWebHandler.WebBrowser.UrlGetToString`: check the available `UrlGet*` methods.