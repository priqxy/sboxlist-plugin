# sboxlist Plugin Wiki - Server Setup, Custom Rewards & Notifications

Reference guide for s&box server owners and gamemode developers using the `sboxlist` library (`prioxystudios.sboxlist`). Verified against s&box `26.11.10a` (`PackageBuild` release whitelist and live multiplayer dedicated server).

---

## 1. Quick Start (No C# Required)

1. **Install the library** into `<your-project>/Libraries/sboxlist/` (so `sboxlist.sbproj` and `Code/` sit directly inside that folder), or add **`prioxystudios.sboxlist`** via the s&box Editor **Library Manager** ([sbox.game/prioxystudios/sboxlist](https://sbox.game/prioxystudios/sboxlist)).
2. **Add `SboxlistService` to your startup scene:**
   - Create an empty `GameObject` (for example `SboxlistHost`) in the scene your server loads on startup.
   - Add the **`SboxlistService`** component.
   - *(Optional, built-in toast banner)* Add the **`SboxlistToastHud`** component to the same `GameObject`. It creates a `ScreenPanel` on clients and renders a dark toast card in the top-right corner whenever a `toast` reward arrives.
3. **Enable Platform Chat (if using default `chat` rewards):**
   - In the s&box Editor, open **Project Settings -> Platform** and turn **Chat Enabled** and **Chat Show UI** on (`ProjectSettings/Platform.config`).
4. **Place `sboxlist.json` on your server disk:**
   - Start the scene once (Play in Editor or boot `sbox-server`). Read the console line:
     ```text
     [sboxlist] config file path: <server-folder>/data/<org>/<package>/sboxlist.json
     ```
     *(For an unpublished local `.sbproj`, the folder is `<server-folder>/data/local/<ident>#local/sboxlist.json`.)*
   - Create `sboxlist.json` at that exact path:
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
   - Run `sbl_reload` in the server console (or restart the server). You will see:
     ```text
     [sboxlist] sboxlist.com: heartbeat accepted
     ```

> **Security rule:** Never put `sboxlist.json` or your `sbs_` secret inside `Assets/`, `Code/`, a `.scene`, a `.prefab`, or a published Workshop package. `FileSystem.Data` lives only on the server's own disk and is never sent to clients.

---

## 2. Overview of Reward Kinds

Server owners configure reward rules in the server-list dashboard (`sboxlist.com -> Your servers -> Rewards`). Each rule sends a `reward` message when a voter is online (immediately on poll) or the next time an offline voter joins the server (`INetworkListener.OnActive`).

| `kind` | Runs on | Needs C# code? | Typical `payload` | Typical `amount` |
|---|---|---|---|---|
| `chat` | Target voter's client (RPC) | No (customizable) | `Thanks for voting, {name}!` | `1` |
| `toast` | Target voter's client (RPC) | No (customizable) | `Vote reward: +{amount} coins` | `100` |
| `console` | Server host (`ConsoleSystem.Run`) | A `[ConCmd]` in your game | `my_give_coins {steamid} {amount}` | `500` |
| `currency` | Server host (`ISboxlistListener`) | Yes (`OnVoteReward`) | `darkrp_cash` or `gems` | `500` |
| `item` | Server host (`ISboxlistListener`) | Yes (`OnVoteReward`) | `weapon_katana` or `starter_crate` | `1` |

### Placeholders
- **`{amount}`**: Integer `1` to `1,000,000`. Allowed in `chat`, `toast`, and `console`.
- **`{steamid}`**: The voter's 17-digit SteamID64. When the voter is online (`Connection player`), `{steamid}` automatically falls back to `player.SteamId` even if they voted by name.
- **`{name}`**:
  - In `chat` / `toast`: replaced with `SboxlistPlayerMessages.ResolveName(player)` (which uses `CustomNameResolver` if configured, or `player.DisplayName`).
  - In `console`: automatically wrapped in double quotes (`"PlayerName"`), falls back to the connected player's display name for Steam voters, and is refused if the name contains quotes, `;`, `` ` ``, `\`, or control characters.

---

## 3. Console Command Rewards (`kind = "console"`)

If your gamemode already has server console commands, define a managed `[ConCmd]` in your game project (s&box blocks native engine commands and `ConVarFlags.Protected` commands from `ConsoleSystem.Run`):

```csharp
using Sandbox;

public static class MyEconomyCommands
{
	[ConCmd( "rp_give_vote_cash" )]
	public static void GiveVoteCash( string steamId, int amount )
	{
		if ( !Networking.IsHost ) return;
		Log.Info( $"Giving ${amount} to SteamID {steamId}" );
		// Look up player or database row by SteamID64 and add cash
	}
}
```

Dashboard rule:
- **Type:** `Console command`
- **What to give (`payload`):** `rp_give_vote_cash {steamid} {amount}`
- **Amount:** `500`
- **Voters:** `Steam voters`

---

## 4. Custom Gamemode Rewards (`currency` & `item`)

For full control over player wallets, inventories, database persistence, and error reporting, implement **`ISboxlistListener`** on any `Component` or `GameObjectSystem` in your active scene.

Because `ISboxlistListener` uses s&box's native `ISceneEvent<T>` index:
- **No manual `+=` / `-=` subscriptions** are needed.
- **Hotload-safe:** modifying and recompiling C# code in the Editor never leaks stale delegates.
- **Dashboard error feedback (`reward.Refuse`):** calling `reward.Refuse("reason")` sends `ok: false` with your message back to `sboxlist.com` (`votes/ack`), so the server owner sees why a reward failed in their dashboard log.

```csharp
using Sandbox;
using Sboxlist;

public sealed class DarkRpVoteRewards : Component, ISboxlistListener
{
	public void OnVoteReward( RewardInfo reward )
	{
		// Mark that our gamemode handles item/currency rewards.
		reward.Handled = true;

		// reward.Player is the active Sandbox.Connection of the player receiving the reward.
		// reward.IdentityKind is "steam" or "name"; reward.IdentityKey is the 17-digit SteamID64 or voter name.
		var player = reward.Player;
		if ( player is null )
		{
			reward.Refuse( "player disconnected before payout" );
			return;
		}

		switch ( reward.Kind )
		{
			case "currency":
				if ( reward.Payload == "darkrp_cash" )
				{
					AddWalletCash( player, reward.Amount );
					SboxlistPlayerMessages.SendTo( player, "toast", $"+${reward.Amount} DarkRP Cash!" );
				}
				else if ( reward.Payload == "vip_tokens" )
				{
					AddVipTokens( player, reward.Amount );
				}
				else
				{
					reward.Refuse( $"unknown currency '{reward.Payload}'" );
				}
				break;

			case "item":
				if ( !TryGiveInventoryItem( player, reward.Payload, (int)reward.Amount, out var reason ) )
				{
					// Reported back to the owner's sboxlist.com Rewards Log (up to 200 chars).
					reward.Refuse( reason );
				}
				break;
		}
	}

	private void AddWalletCash( Connection player, long amount )
	{
		// Your gamemode wallet logic here
	}

	private void AddVipTokens( Connection player, long amount )
	{
		// Your VIP token logic here
	}

	private bool TryGiveInventoryItem( Connection player, string itemId, int count, out string reason )
	{
		reason = null;
		// Return false + reason (e.g. "inventory full" or "unknown item id") when unable to grant
		return true;
	}
}
```

> **Static event alternative:** If your architecture does not use scene components, subscribe to `SboxlistEvents.RewardReceived += OnReward;` (and unsubscribe on shutdown). Both mechanisms share the exact same `RewardInfo` and `reward.Refuse(...)` behavior.

---

## 5. Server-Wide Announcements & Vote Parties (`OnVoteReceived`)

Servers that announce votes to everyone online convert more players into daily voters. `OnVoteReceived( VoteInfo vote )` fires on the host as soon as a vote is polled, **even if the voter is currently offline**.

Use `SboxlistPlayerMessages.BroadcastChat( text )` or `BroadcastToast( text )` to notify every connected client:

```csharp
using Sandbox;
using Sboxlist;

public sealed class VotePartyAnnouncer : Component, ISboxlistListener
{
	private int votesTowardParty;
	private const int PartyGoal = 10;

	public void OnVoteReceived( VoteInfo vote )
	{
		// Ignore owner test votes for public counters.
		if ( vote.IsTest ) return;

		// vote.OnlinePlayer is already resolved when the voter is currently in the server.
		var voterName = vote.OnlinePlayer?.DisplayName
			?? (vote.IdentityKind == "name" ? vote.IdentityKey : "A player");

		votesTowardParty++;
		SboxlistPlayerMessages.BroadcastChat(
			$"★ {voterName} voted on {vote.SiteId}! ({votesTowardParty}/{PartyGoal} until Vote Party)"
		);

		if ( votesTowardParty >= PartyGoal )
		{
			votesTowardParty = 0;
			SboxlistPlayerMessages.BroadcastToast( "VOTE PARTY UNLOCKED! Everyone online gets 2x XP for 15 minutes!" );
			StartDoubleXpEvent();
		}
	}

	private void StartDoubleXpEvent()
	{
		// Trigger server-wide buff or crate drop
	}
}
```

---

## 6. Custom Client Notifications, Razor UI, Sounds & Custom Chatboxes

By default, `chat` and `toast` messages call `Sandbox.Platform.Chat.AddText(...)` on the receiving client. In s&box `26.11.10a`, the built-in `ChatOverlay` fades messages out after **8 seconds** (`MessageFadeTime = 8f`), and many custom gamemodes disable `Platform.Chat` in favor of their own Razor chatbox and HUD.

To take full control on the client, implement **`ISboxlistNotificationListener`** right inside any `.razor` `PanelComponent` (or C# `Component`):

- `notification.Kind` is `"chat"` or `"toast"`.
- `notification.IsBroadcast` is `true` for server-wide `BroadcastChat` / `BroadcastToast` calls, and `false` for personal voter messages.
- Setting **`notification.Handled = true`** suppresses the default `Sandbox.Platform.Chat.AddText(...)` fallback so players never see duplicate lines.

### Example: Custom Razor Toast Banner + Sound + Custom Chatbox (`VoteHud.razor`)

```razor
@using System.Collections.Generic
@using Sandbox
@using Sandbox.UI
@using Sboxlist
@inherits PanelComponent
@implements ISboxlistNotificationListener

<root>
	@foreach ( var item in activeToasts )
	{
		<div class="vote-toast @(item.IsBroadcast ? "broadcast" : "")">
			<span class="badge">@(item.IsBroadcast ? "SERVER" : "REWARD")</span>
			<span class="message">@item.Text</span>
		</div>
	}
</root>

@code
{
	private sealed class ToastItem
	{
		public string Text;
		public bool IsBroadcast;
		public RealTimeUntil ExpiresIn;
	}

	private readonly List<ToastItem> activeToasts = new();

	public void OnVoteNotification( VoteNotification notification )
	{
		// Prevent the default Platform.Chat fallback from also printing this message.
		notification.Handled = true;

		if ( notification.Kind == "chat" )
		{
			// Route into your own custom RP/gamemode chatbox instead of Platform.Chat:
			// MyCustomChatbox.Instance?.AddSystemLine( notification.Text );
			Log.Info( $"[CustomChat] {notification.Text}" );
			return;
		}

		// Play any UI sound event from your project or mounted content:
		Sound.Play( "ui.button.over" );

		activeToasts.Add( new ToastItem
		{
			Text = notification.Text,
			IsBroadcast = notification.IsBroadcast,
			ExpiresIn = 7.0f
		} );
		StateHasChanged();
	}

	protected override void OnUpdate()
	{
		if ( activeToasts.RemoveAll( x => x.ExpiresIn ) > 0 )
		{
			StateHasChanged();
		}
	}
}
```

Matching `VoteHud.razor.scss`:

```scss
VoteHud {
	position: absolute;
	top: 24px;
	right: 24px;
	flex-direction: column;
	gap: 8px;
	z-index: 1000;
	pointer-events: none;

	.vote-toast {
		background-color: #161b21f2;
		border-left: 4px solid #4f46e5;
		padding: 10px 16px;
		align-items: center;
		gap: 10px;

		&.broadcast {
			border-left-color: #f59e0b;
		}

		.badge {
			color: #8ab4ff;
			font-size: 12px;
			font-weight: 700;
		}

		.message {
			color: #ffffff;
			font-size: 15px;
			font-weight: 600;
		}
	}
}
```

---

## 7. Console Commands & Troubleshooting

Run these in the dedicated server console or in the Editor console while in Play mode:

| Command | Purpose |
|---|---|
| `sbl_status` | Prints connection status per site (`online` / `STOPPED`), Vote Party progress, last HTTP status/route, vote & reward counters, and pending queues. |
| `sbl_vote` | Prints the direct public voting page URL for each configured site (`https://sboxlist.com/server/<id>/vote`); in C#, call `SboxlistService.Instance?.SendVoteLink(player)` from your `!vote` chat command. |
| `sbl_reload` | Re-reads `sboxlist.json` without restarting the server. Unchanged running sites keep their deduplication memory and pending acks. |
| `sbl_info` | Calls `GET /v1/info` on sites that offer it and checks server clock skew (requests fail with `401` if clock drift exceeds ±300 s). |
| `sbl_heartbeat` | Forces an immediate heartbeat on the next tick. |
| `sbl_testvote` | Requests a server-initiated test vote on sites that support `/v1/votes/testvote` (on `sboxlist.com`, use **Setup check -> Send a test vote** in the web dashboard). |
| `sbl_hmac_selftest` | Verifies the library's RFC 4231 and protocol v1 HMAC-SHA256 vectors offline. |
| `sbl_debug 1` | Logs every outgoing JSON payload and HTTP response (`0` to turn off). Never logs your server token/secret. |

### Dedicated Server & Pterodactyl Hosting Notes
- **CPU Flags (Proxmox / KVM):** Linux `sbox-server` (`.NET 10` + Source 2) requires `SSE4.2` and `AVX2`. Set your Proxmox VM CPU type to **`host`** (or `x86-64-v3`). Default `kvm64` crashes immediately with `Illegal instruction (exit code 132)`.
- **Pterodactyl `games:source` Startup Command:** In `ghcr.io/pterodactyl/games:source`, `/entrypoint.sh` expands `STARTUP` through ``eval echo $(...)``. Avoid `;`, `&&`, and `$(...)` in the Startup Command (otherwise `./sbox-server` executes inside the subshell and hides console output). Use `/usr/bin/env` directly:
  ```text
  env HOME=/home/container TMPDIR=/home/container/.tmp TERM=xterm LD_LIBRARY_PATH=/home/container/bin/linuxsteamrt64:/home/container/linux64:/home/container ./sbox-server +game {{GAME_PACKAGE}} {{MAP_PACKAGE}} +port {{SERVER_PORT}} +net_query_port {{QUERY_PORT}} +hostname \"{{HOSTNAME}}\" +net_game_server_token \"{{SBOX_SERVER_TOKEN}}\"
  ```
- **Startup Detection & Stop Signal:** Set Pterodactyl's startup `done` string to `"game.load success"` and its **Stop Command** to **`^C`** (`SIGINT`). Because headless Docker PTYs start with `ws_col = 0` (`Console.BufferWidth == 0`), `sbox-server` skips interactive stdin polling (which also avoids `.NET` Unix `\x1b[6n` cursor-position stalls when no browser console is open) and shuts down cleanly in `<1s` on `^C`.
- **Ports & GSLT:** Pass `+port <gameport> +net_query_port <queryport>` (default `27015` and `27016`, never identical) and `+net_game_server_token \"{{SBOX_SERVER_TOKEN}}\"` (an empty string automatically falls back to anonymous Steam logon).

---

## 8. Network, Ports, Reverse Proxies, Custom Names & Workshop FAQ

- **Do I need to open an inbound port for the vote plugin?**
  - **No.** Unlike legacy Votifier plugins that open an inbound TCP port, `SboxlistService` uses **100% outgoing HTTPS (`443/tcp`) pull requests** (`heartbeat`, `votes/poll`, `votes/ack`, `rewards/join`). You only open your standard Source 2 UDP game port (`27015/udp`) and Steam query port (`27016/udp`).
- **Does it work behind NAT, Docker, or a reverse proxy?**
  - **Yes.** Because all plugin traffic is outbound HTTPS from your game server, it works behind home NAT, CGNAT, Docker bridge networks, Pterodactyl Wings, and DDoS-filtered hosts.
  - If you route outbound requests through your own custom HTTPS reverse proxy or connect an additional protocol-v1 site in `sites`, ensure `baseUrl` uses `https://` with a **domain name** (s&box blocks raw IP literals), never returns `301`/`302`/`303` HTTP redirects on `/api/v1/*` routes, and forwards the raw UTF-8 body plus `x-sbl-*` headers unmodified.
- **Does the plugin or my gamemode need to be on the Steam Workshop or s&box Workshop (`sbox.game`)?**
  - s&box distributes libraries and gamemodes through the **s&box Workshop (`sbox.game`)**, not the legacy Garry's Mod Steam Workshop.
  - When you add `prioxystudios.sboxlist` via the Library Manager or unpack `sboxlist-library-1.0.0.zip` into `<your-project>/Libraries/sboxlist/`, the C# library compiles directly into your gamemode package (`0 SB1000` whitelist violations).
  - **Never** put `sboxlist.json` or your `sbs_` secret inside `Assets/` or a published Workshop package. Keep `sboxlist.json` on the dedicated server's local disk inside `FileSystem.Data` (`<server-root>/data/<org>/<game>/sboxlist.json`).
- **How do custom names work (`{name}`, `{steamid}`, custom RP character names & custom currency/item names)?**
  - **Voter names (`{name}`) vs Steam IDs (`{steamid}`):** In `chat`/`toast` rules, `{name}` resolves to `player.DisplayName`. In `console` (`[ConCmd]`) rules, `{steamid}` is used for Steam voters (`17` digits) and `{name}` is used for Name voters (wrapped automatically in double quotes). If a Steam voter's `DisplayName` exceeds 32 Unicode characters, `SboxlistService` automatically sends a **SteamID-only join** (`playerName: null`, `steamId: "7656119..."`) so Steam rewards still pay out reliably.
  - **Custom Roleplay character names:** In `OnVoteReceived(VoteInfo vote)` and `OnVoteReward(RewardInfo reward)`, read `vote.OnlinePlayer` or `reward.Player` (`Sandbox.Connection`) to resolve the player's custom RP character name from your own database before broadcasting to chat.
  - **Custom currency & item names (`payload`):** In `/owner/servers/<id>/rewards`, set any custom identifier (`1..200` characters, such as `darkrp_cash`, `vip_tokens`, or `starter_crate`) in the **What to give** field and match `reward.Payload` in `OnVoteReward`.
