# sboxlist - Server List & Vote Reward Library for s&box (1.0.0)

`sboxlist` (`prioxystudios.sboxlist`) is an open-source C# library for s&box dedicated servers and hosted lobbies. It sends signed heartbeats, polls and acknowledges player votes, and delivers in-game rewards when a voter is online or next joins the server.

It implements Wire Protocol v1 (`PROTOCOL.md`): `HMAC-SHA256` (`sbs_` secrets) used by [sboxlist.com](https://sboxlist.com), plus multi-site bearer/HMAC endpoints for any compatible server list.

- **s&box Package:** [`prioxystudios.sboxlist`](https://sbox.game/prioxystudios/sboxlist)
- **GitHub Repository:** [`https://github.com/priqxy/sboxlist-plugin`](https://github.com/priqxy/sboxlist-plugin)

---

## Quick Install

1. **Via s&box Editor (Library Manager):** Add **`prioxystudios.sboxlist`** to your project's Library References, **or** extract `sboxlist-library-1.0.0.zip` into `<your-project>/Libraries/sboxlist/` (so `sboxlist.sbproj` and `Code/` sit directly inside `Libraries/sboxlist/`).
2. **Add the scene component:** In your startup scene (next to `NetworkHelper`), add **`SboxlistService`** to a GameObject. Optionally add **`SboxlistToastHud`** next to it for built-in on-screen vote reward banners.
3. **Drop your server config (`sboxlist.json`):** Place `sboxlist.json` inside `FileSystem.Data` on your server (`<server-root>/data/<org>/<game>/sboxlist.json`). On startup, `[sboxlist] config file path: ...` logs the exact path in the server console.

---

## Configuration (`sboxlist.json`)

Never store `sboxlist.json` inside `Assets/`, a `.scene`, or a published package. Keep it in `FileSystem.Data` on the server host.

### `sboxlist.com` (Protocol v1, HMAC-SHA256)

```json
{
  "sites": [
    {
      "siteId": "sboxlist.com",
      "baseUrl": "https://sboxlist.com/api",
      "serverId": "REPLACE-WITH-THE-SERVER-UUID-SHOWN-ON-SBOXLIST",
      "token": "sbs_REPLACE-WITH-THE-SERVER-SECRET-SHOWN-ON-SBOXLIST",
      "auth": "hmac",
      "rewards": true
    }
  ],
  "debug": false
}
```

### Multi-Site Configuration

A single server can report to multiple server lists at the same time. Add one entry per site inside `"sites"`:

- `baseUrl`: `https://` URL with a domain name (s&box blocks raw IP addresses; `http://localhost` on ports `80`, `443`, `8080`, `8443` is allowed for local testing).
- `serverId`: Server identifier assigned by the list (`sboxlist.com` uses your server UUID).
- `token`: Per-server secret (`sbs_...` or `sbt_...` followed by 43 base64url characters). Placeholder tokens are refused automatically.
- `auth`: `"hmac"` (default; required by `sboxlist.com`) or `"bearer"` (for lists using bearer tokens).
- `rewards`: `true` (default) to deliver configured rewards when a voter is online or joins; `false` to acknowledge votes without running rewards.

### Alternative: Command-Line ConVars (Single Site)

Instead of a JSON file, you can pass hidden ConVars on the dedicated server command line (not saved to disk and never replicated to clients):

```text
+sbl_base_url https://sboxlist.com/api +sbl_server_id <uuid> +sbl_token sbs_<secret> +sbl_auth hmac
```

---

## Custom Gamemode Rewards & Custom UI (`ISceneEvent<T>`)

Server owners who only need chat messages, toast banners, or console commands can configure them directly on the website without writing C# code. Gamemode authors (DarkRP, TTT, Jailbreak, Sandbox, Survival) can hook into votes and rewards using s&box scene interfaces:

```csharp
using Sandbox;
using Sboxlist;

// 1. Server-side (host): custom currency/items, Refusal reporting & server-wide Vote Parties
public sealed class MyVoteRewards : Component, ISboxlistListener
{
	public void OnVoteReceived( VoteInfo vote )
	{
		if ( !vote.IsTest )
		{
			var who = vote.OnlinePlayer?.DisplayName ?? vote.IdentityKey;
			SboxlistPlayerMessages.BroadcastChat( $"★ {who} voted for the server on {vote.SiteId}!" );
		}
	}

	public void OnVoteReward( RewardInfo reward )
	{
		if ( reward.Kind == "currency" )
		{
			reward.Handled = true;
			// Credit reward.Player (Connection) with reward.Amount of reward.Payload (e.g. "cash")
			return;
		}

		if ( reward.Kind == "item" )
		{
			if ( reward.Payload != "starter_crate" )
			{
				// Reports ok: false + reason back to the server list's owner reward log
				reward.Refuse( $"Unknown item id '{reward.Payload}'" );
				return;
			}

			reward.Handled = true;
			// Grant reward.Amount x reward.Payload to reward.Player
		}
	}
}

// 2. Client-side (.razor Panel or PanelComponent): custom HUD banner, sound & custom chatbox
public sealed class MyCustomVoteHud : PanelComponent, ISboxlistNotificationListener
{
	public void OnVoteNotification( VoteNotification notification )
	{
		notification.Handled = true; // suppresses the default Platform.Chat line
		// Render notification.Kind ("chat" | "toast"), notification.IsBroadcast, notification.Text
	}
}
```

Static C# events (`SboxlistEvents.VoteReceived`, `RewardReceived`, `NotificationReceived`, `ToastShown`) are also available. See the Developer Wiki (`docs/DEVELOPER-WIKI.md`) for DarkRP economy, Vote Party, Razor `.razor` + `.scss` HUD, and Pterodactyl hosting guides.

---

## Console Commands

Run in the dedicated server console or in the s&box Editor console during Play mode:

| Command | Description |
|---|---|
| `sbl_status` | Per site: `online` / `STOPPED`, Vote Party progress, last HTTP status & route, vote/reward counters, queue depths |
| `sbl_vote` | Prints the server's direct voting page URL (`https://sboxlist.com/server/<id>/vote`); in C#, call `SboxlistService.Instance?.SendVoteLink(player)` |
| `sbl_reload` | Reloads `sboxlist.json` live without dropping seen-vote deduplication or pending acks |
| `sbl_heartbeat` | Triggers an immediate heartbeat on the next tick |
| `sbl_info` | Queries `GET /v1/info` on lists that expose it and checks clock drift (±300 s window) |
| `sbl_testvote` | Requests a server-side test vote on lists that expose `/v1/votes/testvote` (on `sboxlist.com`, use **Setup check -> Send a test vote** in the owner dashboard) |
| `sbl_hmac_selftest` | Runs offline RFC 4231 & protocol v1 HMAC-SHA256 self-tests |
| `sbl_test_reward <steamid> <amount>` | Built-in safe target command for testing `console` rewards |
| `sbl_debug 1` | Verbose request/response logging (`0` to disable; never prints secrets) |

---

## Implementation Notes

- **s&box Whitelist (`0 SB1000`):** Implements RFC 2104 HMAC-SHA256 over whitelisted `System.Security.Cryptography.SHA256.HashData` and `Guid.NewGuid` nonces (does not reference blocked `HMACSHA256`, `RandomNumberGenerator`, or `Task.Run`).
- **Host-Only Execution & RPC Scoping:** `SboxlistService` runs only on `Networking.IsHost`. Player notifications use `[Rpc.Broadcast( NetFlags.Reliable | NetFlags.HostOnly )]` scoped to the recipient via `Rpc.FilterInclude`.
- **Placeholder Validation:** `console` rewards reject `;`, newlines, quotes, backticks, `$`, `&`, `|`, `>`, `<`, and control characters before invoking `ConsoleSystem.Run`.

---

## License

Released under the [MIT License](./LICENSE).
