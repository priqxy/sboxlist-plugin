# sboxlist Plugin Protocol Specification (Wire Protocol v1)

This document specifies the open HTTPS pull protocol between an **s&box game server** running the `sboxlist` library (`prioxystudios.sboxlist`, `1.0.0`) and any compatible **server-list website**.

Nothing in this protocol locks a server to a single website: a server can report to multiple server lists simultaneously, each with its own `siteId`, `baseUrl`, `serverId`, and secret token. Section 13 documents the exact `sboxlist.com` wire profile alongside the generic multi-site extensions.

Keywords **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are to be interpreted as described in RFC 2119.

---

## 1. Roles

- **Server:** an s&box dedicated server (or hosted multiplayer lobby) running `SboxlistService`. It opens **outgoing HTTPS requests only** and never opens an inbound listening port for the website.
- **Site:** a server-list website that issues a `serverId` and per-server secret/token, accepts heartbeats, queues player votes, and computes reward messages on player join or online poll.
- **Owner:** the server operator who claims the server on the site and configures reward rules.

---

## 2. Transport

- **HTTPS `POST`** with a UTF-8 JSON body for all state-changing routes, plus optional **`GET /v1/info`**. No WebSockets and no inbound ports.
- `baseUrl` **MUST** use `https://` with a **domain name** (for example `https://sboxlist.com/api`). s&box blocks raw IP addresses and domain names resolving to private/loopback ranges (`http://localhost` on ports `80`, `443`, `8080`, and `8443` is allowed by the engine for local testing, or any port when launched with `-allowlocalhttp`).
- Requests carry `Content-Type: application/json; charset=utf-8` on the `HttpContent` payload, at most **16,384 bytes**.
- Sites **MUST NOT** redirect API routes (`301`/`302`/`303` convert `POST` into `GET` and strip `Authorization` inside s&box's HTTP client).
- Responses are UTF-8 JSON objects and include `x-sbl-server-time` (Unix seconds) when supported.
- The server enforces a per-request timeout (`10 s`) and never runs two concurrent requests to the same route on the same site.

---

## 3. Versioning & Forward Compatibility

- Request bodies carry `"v": 1`. Unsupported versions return `400 unsupported_version`.
- Routes are prefixed with `/v1/...` relative to `baseUrl` (so `https://sboxlist.com/api` + `/v1/heartbeat` resolves to `https://sboxlist.com/api/v1/heartbeat`).
- Both sides **MUST** ignore unknown JSON properties.
- Unknown message `type` entries inside a poll response **MUST** be ignored and acknowledged with `ok: false`, `error: "unsupported"`.

---

## 4. Identifiers & Formats

| Field | Format |
|---|---|
| `siteId` | Lowercase `[a-z0-9][a-z0-9.-]{0,62}`, typically the site's domain (`sboxlist.com`) |
| `serverId` | `[A-Za-z0-9_-]{1,64}` (`sboxlist.com` uses lowercase UUIDs) |
| `token` / secret | `sbs_` or `sbt_` followed by 43 base64url characters (32 random bytes), shown once to the owner |
| Message `id` | `[A-Za-z0-9_-]{1,64}`, unique per site; test votes start with `test-` |
| Timestamp | Unix seconds, ASCII digits only |
| Nonce | 16 to 64 characters of `[A-Za-z0-9_-]`, unique per request (`Guid.NewGuid().ToString("N")` -> 32 hex chars) |
| SteamID64 | 17-digit individual SteamID64 (`7656119...`), **encoded as a JSON string** |
| Timestamps in JSON | ISO 8601 UTC string, e.g. `2026-11-17T12:00:00.000Z` |

---

## 5. Authentication

Every `POST` request carries:

| Header | Value |
|---|---|
| `x-sbl-server` | Server ID (must equal `serverId` in the JSON body) |
| `x-sbl-timestamp` | Request creation time in Unix seconds |
| `x-sbl-nonce` | Fresh per-request nonce |
| `x-sbl-protocol` | `1` |

plus **either** `x-sbl-signature` (HMAC-SHA256) **or** `Authorization: Bearer ...` (never both on the same request).

The site stores only `sha256(token)` (32 bytes / 64 lowercase hex chars) and never stores the raw token in plaintext.

### 5.1 HMAC-SHA256 (`"auth": "hmac"`, Default, Required by `sboxlist.com`)

```text
x-sbl-signature: <64 lowercase hex characters>
```

```text
key       = 32 raw bytes of SHA-256(UTF-8(token))
canonical = "sbl1\n" + serverId + "\n" + timestamp + "\n" + nonce + "\n" + hex(SHA-256(rawBodyBytes))
signature = hex(HMAC-SHA256(key, UTF-8(canonical)))
```

- Because `System.Security.Cryptography.HMACSHA256` is not on the s&box sandbox whitelist (`SB1000`), `sboxlist` computes RFC 2104 `HMAC-SHA256` directly over whitelisted `SHA256.HashData`:
  ```text
  K0   = key padded with zero bytes to 64 bytes
  HMAC = SHA256((K0 XOR 0x5c) || SHA256((K0 XOR 0x36) || message))
  ```
- The token never travels over the network; an intercepted request cannot be forged or replayed on another route (`type` is checked against the endpoint).

### 5.2 Bearer Token (`"auth": "bearer"`, Optional for Generic Lists)

```text
Authorization: Bearer sbt_<43 characters>
```

The site verifies header shapes, checks that `x-sbl-timestamp` is within **±300 s** of server time, compares `sha256(token)` in constant time, and records `x-sbl-nonce` for **601 s** per server.

---

## 6. Wire Messages

### 6.1 `GET /v1/info` (Optional Discovery & Clock Check)

```json
{
  "ok": true,
  "protocol": [1],
  "siteId": "example.org",
  "name": "Example List",
  "auth": ["bearer", "hmac"],
  "serverTime": 1794350743
}
```

Sites that do not expose `/v1/info` return `404`; the library treats `404` on `/v1/info` as "not offered" without stopping the site client.

### 6.2 `POST /v1/heartbeat` (Every 60 s)

```json
{
  "v": 1,
  "type": "heartbeat",
  "serverId": "3f2a9c1e-5b7d-4e8a-9c0d-1a2b3c4d5e6f",
  "sequence": 1794350749215,
  "players": 12,
  "maxPlayers": 64,
  "bots": 0,
  "map": "facepunch.flatgrass",
  "package": "facepunch.sandbox",
  "build": "",
  "uptimeSeconds": 3600,
  "serverTime": "2026-11-17T12:00:00.000Z",
  "serverTimestamp": "2026-11-17T12:00:00.000Z",
  "plugin": "sboxlist/1.0.0"
}
```

- `sequence` **MUST** strictly increase (`max(previous + 1, unixMilliseconds)` survives restarts without disk state).
- Sending both `serverTimestamp` and `serverTime` ensures compatibility with `sboxlist.com` v1 and generic v0.2 lists.
- Response: `200 {"ok":true,"status":"online","nextHeartbeatSeconds":60}`.

### 6.3 `POST /v1/votes/poll` (Every 15 s)

Request: `{"v":1,"type":"poll","serverId":"<id>"}`.

Response (`messages` entries may be JSON objects or JSON-encoded strings; the library parses both):

```json
{
  "ok": true,
  "pollAfterSeconds": 15,
  "messages": [
    {
      "v": 1,
      "type": "vote",
      "id": "3f0c9a7e-2b1d-4c6e-9a8f-0d1e2f3a4b5c",
      "site": "sboxlist.com",
      "identity": { "kind": "steam", "key": "76561197960287930" },
      "at": "2026-11-17T12:00:00.000Z",
      "test": false
    }
  ]
}
```

- The library deduplicates `(siteId, vote.id)` in memory (up to 2,000 recent IDs / 24 hours) so retried deliveries are acknowledged again without double-triggering events or rewards.

### 6.4 `POST /v1/votes/ack`

```json
{
  "v": 1,
  "type": "ack",
  "serverId": "<id>",
  "acks": [
    { "id": "3f0c9a7e-2b1d-4c6e-9a8f-0d1e2f3a4b5c", "ok": true },
    { "id": "7a1e4c20-9d3b-4f5a-8e6c-2b9d0f1a3c47", "ok": true, "kind": "reward" }
  ]
}
```

- `1` to `50` items per batch.
- `kind` is `"vote"` (default) or `"reward"`.
- Failed or refused rewards send `"ok": false` with `"error"` (up to 200 characters; gamemode listeners set this via `RewardInfo.Refuse(reason)`).

### 6.5 `POST /v1/rewards/join`

Sent when a player finishes joining (`INetworkListener.OnActive`, `reason: "join"`) or immediately when a vote arrives for an already-connected player (`reason: "vote"`):

```json
{
  "v": 1,
  "type": "join",
  "serverId": "<id>",
  "reason": "join",
  "playerName": "Alex",
  "steamId": "76561197960287930"
}
```

- `playerName` is `Connection.DisplayName` from the host (`1..32` Unicode code points with no NUL or lone surrogates). If a player's Steam display name exceeds 32 code points, `playerName` is sent as `null` alongside `steamId` (**SteamID-only join**) so Steam votes are still rewarded reliably.
- Dedicated server pseudo-host connections (`Application.IsDedicatedServer && channel.IsHost`) and non-individual Steam IDs are ignored.

### 6.6 `reward` Message (Returned Inside `rewards/join`)

```json
{
  "v": 1,
  "type": "reward",
  "id": "7a1e4c20-9d3b-4f5a-8e6c-2b9d0f1a3c47",
  "voteId": "3f0c9a7e-2b1d-4c6e-9a8f-0d1e2f3a4b5c",
  "ruleId": "5c2d8e1f-0a4b-4e7c-9d2f-6b1a3e8c0d59",
  "identity": { "kind": "steam", "key": "76561197960287930" },
  "kind": "toast",
  "payload": "Vote reward: +{amount} coins",
  "amount": 100
}
```

| `kind` | Action on the Server |
|---|---|
| `chat` | Sends a targeted host-only RPC to the voter (`ISboxlistNotificationListener` or `Sandbox.Platform.Chat.AddText`) |
| `toast` | Sends a targeted host-only RPC to the voter (`SboxlistToastHud`, `ISboxlistNotificationListener`, or chat fallback) |
| `console` | Validates placeholders and runs `ConsoleSystem.Run` on the host main thread (managed `[ConCmd]` commands only) |
| `currency`, `item` | Dispatches `ISboxlistListener.OnVoteReward(RewardInfo)` (and `SboxlistEvents.RewardReceived`) on the host |

---

## 7. Multi-Site Isolation & Secret Storage

- Each entry in `sites` runs an independent `SiteClient` loop, backoff timer, deduplication window, and ack queue.
- Store `sboxlist.json` in `FileSystem.Data` (`<server-root>/data/<org>/<package>/sboxlist.json`), never inside `Assets/`, `.scene`, `.prefab`, or published Workshop archives.

---

## 8. HTTP Status Handling & Rate Limits

| HTTP | Meaning | Client Behavior |
|---|---|---|
| `400` (`unsupported_version`) | Unsupported protocol `v` | Stop site client and log upgrade notice |
| `400` (`bad_request` / `invalid_payload`) | Malformed payload | Log once, drop that request without retrying |
| `401` (`unauthorized`) | Invalid secret or clock drift > ±300 s | Pause site client for 60 s, log check hint, then retry |
| `404` (`not_found`) | Wrong `baseUrl` (except optional `info`/`testvote`) | Stop site client and log URL |
| `409` (`stale_sequence`) | Non-increasing heartbeat sequence | Ignore; next heartbeat uses higher sequence |
| `410` (`server_gone`) | Server removed from list | Stop site client until reload/restart |
| `429` (`too_soon`) | Rate limited | Wait `retryAfterSeconds` body field or `Retry-After` HTTP header |
| `5xx` / timeout / network error | Temporary outage | Exponential backoff (`15s`, `30s`, `60s`, `120s`, `300s` max + jitter) |

---

## 9. Limits Summary

| Parameter | Limit |
|---|---|
| Max request body | `16,384` bytes |
| Max single message length | `4,096` characters |
| Clock skew window | `±300 s` (`nonce` TTL `601 s`) |
| Heartbeat interval | `60 s` default (`20 s` minimum) |
| Poll interval | `15 s` default (`5..300 s`) |
| Unacknowledged votes in flight | `20` |
| Acks per batch | `1..50` |
| Reward `amount` | `1..1,000,000` |
| Reward `payload` / `error` | `1..200` Unicode code points, no control chars or lone surrogates |

---

## 10. Test Vectors

1. **RFC 4231 Test Case 2:**
   - Key: `"Jefe"`
   - Data: `"what do ya want for nothing?"`
   - Expected `HMAC-SHA256`: `5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843`
2. **Protocol v1 Request Signature (`sbl_hmac_selftest`):**
   - Secret: `"sbs_" + 43 × "A"`
   - `serverId`: `"3f2a9c1e-5b7d-4e8a-9c0d-1a2b3c4d5e6f"`
   - `timestamp`: `"1790000000"`
   - `nonce`: `"abcdefghijklmnop"`
   - Body: `{"v":1,"type":"heartbeat"}`
   - Expected `x-sbl-signature`: `62c1de08862566ae6cc2008bcef3a3bccb56c6e0e53897f48fc08883ae2cccfd`

---

## 11. s&box Engine & Whitelist Notes (Verified on `26.11.10a`)

- **Allowed Crypto & Primitives:** `SHA256.HashData`, `Guid.NewGuid`, `Convert.ToHexString`, `DateTimeOffset.UtcNow.ToUnixTimeSeconds`, `Stopwatch`, `Task.Delay`, `System.Text.Json`. Blocked (`SB1000`): `HMACSHA256`, `RandomNumberGenerator`, `Task.Run`, `HttpClient`, `System.IO.File`, `response.Headers.RetryAfter` (use `response.Headers.TryGetValues("Retry-After", ...)` instead).
- **Targeted Client RPCs:** Static `[Rpc.Broadcast(NetFlags.Reliable | NetFlags.HostOnly)]` wrapped in `using (Rpc.FilterInclude(connection))` delivers reward notifications exclusively to the target voter.

---

## 12. `sboxlist.com` Profile Summary

| Aspect | `sboxlist.com` (`https://sboxlist.com/api`) | Generic Multi-Site Lists |
|---|---|---|
| Authentication | `"hmac"` (`x-sbl-signature`, `sbs_` secret) | `"bearer"` (`sbt_` token) or `"hmac"` |
| `serverId` | Server UUID | `[A-Za-z0-9_-]{1,64}` |
| Poll & join `messages` | JSON-encoded strings (parsed automatically) | JSON objects or strings |
| Heartbeat timestamp | `serverTimestamp` (library sends both) | `serverTime` / `serverTimestamp` |
| Test votes | Triggered from **Setup check -> Send a test vote** in the web dashboard | Dashboard or `POST /v1/votes/testvote` |
