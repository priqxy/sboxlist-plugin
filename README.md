# sboxlist - Vote & Heartbeat Plugin for s&box (1.0.0)

`sboxlist` ([`prioxystudios.sboxlist`](https://sbox.game/prioxystudios/sboxlist)) is an open-source C# library for s&box dedicated servers and hosted lobbies. It connects a server to one or more server-list websites over outgoing HTTPS:

- **Live server heartbeats** (`POST /v1/heartbeat`)
- **Vote polling & acknowledgements** (`POST /v1/votes/poll`, `POST /v1/votes/ack`)
- **Online & offline vote payouts** (`POST /v1/rewards/join`)
- **Built-in reward types** (`chat`, `toast` with optional `SboxlistToastHud`, and `console` `[ConCmd]` placeholders)
- **C# gamemode hooks** (`ISboxlistListener` & `ISboxlistNotificationListener` via `ISceneEvent<T>`) for custom currencies, items, Vote Parties, and custom Razor HUDs/chatboxes
- **Multi-site support** (`sboxlist.com` protocol v1 HMAC-SHA256 and any server list implementing [`PROTOCOL.md`](./PROTOCOL.md))

---

## Repository Layout

| Path | Description |
|---|---|
| [`sboxlist/`](./sboxlist) | The s&box library project (`sboxlist.sbproj`, `Code/*.cs`, `sboxlist.example.json`, `thumb.png`) |
| [`docs/DEVELOPER-WIKI.md`](./docs/DEVELOPER-WIKI.md) | Server setup, `sboxlist.json`, `console` commands, `ISboxlistListener` (`currency` / `item` + `Refuse`), Vote Parties, custom Razor `.razor` HUDs, and Pterodactyl hosting |
| [`PROTOCOL.md`](./PROTOCOL.md) | Wire protocol specification (protocol v1 HMAC-SHA256 & multi-site bearer/HMAC) |
| [`docker/pterodactyl-egg-sbox.json`](./docker/pterodactyl-egg-sbox.json) | Pterodactyl v2 Egg for Linux `sbox-server` (`App 1892930`) |
| [`LICENSE`](./LICENSE) | MIT License |

---

## Quick Start

1. **Install the library:**
   - **Via s&box Editor Library Manager:** add **`prioxystudios.sboxlist`** ([sbox.game/prioxystudios/sboxlist](https://sbox.game/prioxystudios/sboxlist)) to your game project's Library References, **or**
   - **Manual copy:** place the [`sboxlist/`](./sboxlist) folder into `<your-project>/Libraries/sboxlist/`.
2. **Add the component to your startup scene:**
   - Create an empty `GameObject` in your startup scene and attach **`SboxlistService`**.
   - *(Optional)* Attach **`SboxlistToastHud`** to render top-right reward banners without writing custom UI code.
3. **Configure `sboxlist.json` on your server disk:**
   - Boot the server once and check the console for:
     ```text
     [sboxlist] config file path: <server-root>/data/<org>/<game>/sboxlist.json
     ```
   - Create `sboxlist.json` at that path (never inside `Assets/` or a published Workshop package):
     ```json
     {
       "sites": [
         {
           "siteId": "sboxlist.com",
           "baseUrl": "https://sboxlist.com/api",
           "serverId": "YOUR-SERVER-UUID-FROM-SBOXLIST",
           "token": "sbs_YOUR_ONE_TIME_SERVER_SECRET",
           "auth": "hmac",
           "rewards": true
         }
       ],
       "debug": false
     }
     ```
   - Run `sbl_reload` in the server console (or restart) and verify with `sbl_status`.

---

## Documentation

- **Gamemode Developers & Server Owners:** read [`docs/DEVELOPER-WIKI.md`](./docs/DEVELOPER-WIKI.md) for DarkRP currency/inventory handlers, server-wide Vote Party announcements, custom `.razor` + `.scss` notification panels, and Linux/Pterodactyl dedicated server configuration.
- **Server-List Operators:** read [`PROTOCOL.md`](./PROTOCOL.md) to implement a compatible `/v1/` backend.

---

## License

Released under the [MIT License](./LICENSE). Not affiliated with Facepunch Studios.
