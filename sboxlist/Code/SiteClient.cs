using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>
/// Talks to one site (PROTOCOL.md section 6): heartbeat every 60 s, poll every 15 s, acks right
/// after, a join per player, test votes on request. Runs on the host only, from the main thread
/// (SboxlistService.OnUpdate); never two requests of the same kind at once.
/// </summary>
public sealed class SiteClient
{
	/// <summary>The largest body a site accepts (PROTOCOL.md section 2).</summary>
	internal const int MaxBodyBytes = 16384;

	/// <summary>One ack batch stays well under MaxBodyBytes, so 50 long acks still fit.</summary>
	const int MaxAckBatchBytes = 12000;

	/// <summary>How often a join (reward request) is tried before it is given up.</summary>
	const int MaxJoinAttempts = 3;

	/// <summary>What a site has already delivered. Kept per site and server id, so a reload or a restarted site does not pay twice (this process only).</summary>
	sealed class Dedupe
	{
		public readonly HashSet<string> Votes = new();
		public readonly Queue<string> VoteOrder = new();
		public readonly HashSet<string> Rewards = new();
		public readonly Queue<string> RewardOrder = new();
	}

	static readonly Dictionary<string, Dedupe> dedupes = new();

	public SiteConfig Config { get; }

	public int LastStatus { get; private set; }
	public string LastRoute { get; private set; } = "";
	public string LastError { get; private set; } = "";
	public bool HeartbeatAccepted { get; private set; }
	public bool Disabled { get; private set; }
	public string DisabledReason { get; private set; } = "";
	public int VotesReceived { get; private set; }
	public int RewardsRun { get; private set; }
	public string LastVoteId { get; private set; } = "";
	public double? ClockOffsetSeconds { get; private set; }

	readonly SboxlistService service;
	readonly byte[] hmacKey;
	readonly Dedupe dedupe;

	long lastSequence;
	double nextHeartbeatAt;
	double nextPollAt;
	double pausedUntil;
	double ackRetryAt;
	double joinRetryAt;
	int ackLimit = 50;
	int failures;
	bool heartbeatBusy, pollBusy, ackBusy, joinBusy, testvoteBusy, infoBusy;

	readonly List<JsonObject> pendingAcks = new();
	readonly Queue<JoinRequest> joins = new();

	sealed class JoinRequest
	{
		public Connection Player;
		public string Reason;
		public int Attempts;
	}

	internal sealed class Reply
	{
		public string Route = "";
		public int Status;
		public long? RetryAfterSeconds;
		public JsonNode Json;
		public string Text = "";
		public string Error = "";
		public bool Ok => Status >= 200 && Status < 300;
	}

	public SiteClient( SiteConfig config, SboxlistService service )
	{
		Config = config;
		this.service = service;
		hmacKey = SboxlistCrypto.KeyFromToken( config.Token );

		var key = config.SiteId + "|" + config.ServerId;
		if ( !dedupes.TryGetValue( key, out var found ) )
		{
			found = new Dedupe();
			dedupes[key] = found;
		}
		dedupe = found;
	}

	double Now => RealTime.NowDouble;

	public void Tick()
	{
		if ( Disabled || Now < pausedUntil ) return;

		if ( !heartbeatBusy && Now >= nextHeartbeatAt ) _ = HeartbeatAsync();
		if ( !pollBusy && Now >= nextPollAt ) _ = PollAsync();
		if ( !ackBusy && pendingAcks.Count > 0 && Now >= ackRetryAt ) _ = FlushAcksAsync();
		if ( !joinBusy && joins.Count > 0 && Now >= joinRetryAt ) _ = JoinAsync( joins.Dequeue() );
	}

	public void ForceHeartbeat()
	{
		nextHeartbeatAt = 0;
		pausedUntil = 0;
	}

	public void QueueJoin( Connection player, string reason )
	{
		if ( player is null || !Config.Rewards || Disabled ) return;
		joins.Enqueue( new JoinRequest { Player = player, Reason = reason } );
	}

	// ---------------------------------------------------------------- requests

	string Url( string route ) => Config.BaseUrl + "/v1/" + route;

	internal async Task<Reply> SendAsync( string route, JsonObject body )
	{
		body["v"] = 1;
		body["serverId"] = Config.ServerId;
		var json = body.ToJsonString();
		var bytes = Encoding.UTF8.GetBytes( json );
		var reply = new Reply { Route = route };

		if ( bytes.Length > MaxBodyBytes )
		{
			// A bug in what we send (the ack batches are sized to prevent it): refused without a request.
			reply.Status = 413;
			reply.Error = $"the body is {bytes.Length} bytes; the limit is {MaxBodyBytes}";
			LastRoute = route;
			LastStatus = reply.Status;
			return reply;
		}

		var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString( CultureInfo.InvariantCulture );
		var nonce = SboxlistCrypto.NewNonce();

		var headers = new Dictionary<string, string>
		{
			["x-sbl-server"] = Config.ServerId,
			["x-sbl-timestamp"] = timestamp,
			["x-sbl-nonce"] = nonce,
			["x-sbl-protocol"] = "1",
		};

		if ( Config.Auth == "hmac" )
			headers["x-sbl-signature"] = SboxlistCrypto.Signature( hmacKey, Config.ServerId, timestamp, nonce, bytes );
		else
			headers["Authorization"] = "Bearer " + Config.Token;

		if ( SboxlistConfig.Debug ) Log.Info( $"[sboxlist] {Config.SiteId} -> POST {route} {json}" );

		try
		{
			using var cts = new CancellationTokenSource( TimeSpan.FromSeconds( 10 ) );
			using var content = new StringContent( json, Encoding.UTF8, "application/json" );
			using var response = await Http.RequestAsync( Url( route ), "POST", content, headers, cts.Token );
			reply.Status = (int)response.StatusCode;
			if ( response.Headers.TryGetValues( "Retry-After", out var retryValues ) )
			{
				foreach ( var val in retryValues )
				{
					if ( val is not null && long.TryParse( val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retrySec ) )
					{
						reply.RetryAfterSeconds = retrySec;
						break;
					}
				}
			}
			reply.Text = await response.Content.ReadAsStringAsync();
			reply.Json = JsonRead.Parse( reply.Text );
		}
		catch ( Exception e )
		{
			reply.Status = 0;
			reply.Error = e.Message;
		}

		LastRoute = route;
		LastStatus = reply.Status;
		if ( SboxlistConfig.Debug ) Log.Info( $"[sboxlist] {Config.SiteId} <- {route} {reply.Status} {(reply.Status == 0 ? reply.Error : reply.Text)}" );
		return reply;
	}

	/// <summary>Turns a failed answer into "wait this long" (seconds), and stops the site when retrying is pointless.</summary>
	double Failed( Reply r )
	{
		var error = JsonRead.Get( r.Json, "error" );
		var code = JsonRead.Str( error, "code" ) ?? "";
		var message = JsonRead.Str( error, "message" ) ?? r.Error;
		var field = JsonRead.Str( error, "field" );
		var hint = JsonRead.Str( error, "hint" );
		LastError = $"{r.Route}: HTTP {r.Status} {code} {message}{(field is null ? "" : $" (field {field})")}{(hint is null ? "" : $" ({hint})")}".Trim();

		switch ( r.Status )
		{
			case 0:
			case >= 500:
				failures++;
				var wait = Backoff( failures );
				Log.Warning( $"[sboxlist] {Config.SiteId}: {LastError}; trying again in {wait:0} s" );
				return wait;

			case 429:
				// Protocol v0.2 sends retryAfterSeconds in the body; sboxlist.com (v1) sends only the Retry-After header (PROTOCOL.md 8, 14).
				var retry = JsonRead.Long( error, "retryAfterSeconds" ) ?? r.RetryAfterSeconds ?? 5;
				return Math.Clamp( retry, 1, 300 );

			case 401:
				var tip = Config.Auth == "bearer" ? " sboxlist.com accepts hmac only: set auth to hmac." : "";
				if ( Now >= pausedUntil )
					Log.Warning( $"[sboxlist] {Config.SiteId}: refused (401). Check the token, the auth setting and the server clock.{tip}" );
				pausedUntil = Now + 60;
				return 60;

			case 404:
			case 410:
				Stop( LastError );
				return 300;

			case 400 when code == "unsupported_version":
				Stop( "the site does not speak protocol 1; update the plugin" );
				return 300;

			case 400 when field == "serverId":
				Stop( "the site refuses this serverId; use the id the site shows (sboxlist.com needs a UUID)" );
				return 300;

			default:
				Log.Warning( $"[sboxlist] {Config.SiteId}: {LastError}" );
				return 60;
		}
	}

	/// <summary>Trouble that may pass: no answer, a rate limit, refused for now (401), a server error.</summary>
	static bool Retryable( int status ) => status == 0 || status == 401 || status == 429 || status >= 500;

	/// <summary>15, 30, 60, 120, then 300 s, with up to 20 % jitter (PROTOCOL.md section 8).</summary>
	static double Backoff( int failures )
	{
		double[] steps = { 15, 30, 60, 120, 300 };
		var step = steps[Math.Min( Math.Max( failures, 1 ) - 1, steps.Length - 1 )];
		var jitter = Guid.NewGuid().ToByteArray()[0] / 255.0 * 0.2;
		return Math.Min( 300, step * (1 + jitter) );
	}

	void Stop( string reason )
	{
		Disabled = true;
		DisabledReason = reason;
		Log.Warning( $"[sboxlist] {Config.SiteId}: stopped until sbl_reload or a restart: {reason}" );
	}

	void ClearErrorFrom( string route )
	{
		if ( LastError.StartsWith( route + ":" ) ) LastError = "";
	}

	// ---------------------------------------------------------------- heartbeat

	async Task HeartbeatAsync()
	{
		heartbeatBusy = true;
		try
		{
			var now = DateTimeOffset.UtcNow;
			lastSequence = Math.Max( lastSequence + 1, now.ToUnixTimeMilliseconds() );
			var players = service.PlayerCount();
			var maxPlayers = Math.Clamp( Math.Max( Networking.MaxPlayers, players ), 1, 1024 );
			var stamp = now.UtcDateTime.ToString( "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture );

			var body = new JsonObject
			{
				["type"] = "heartbeat",
				["sequence"] = lastSequence,
				["players"] = Math.Min( players, maxPlayers ),
				["maxPlayers"] = maxPlayers,
				["bots"] = 0,
				["map"] = Clip( service.MapName, 128 ),
				["package"] = Clip( Game.Ident ?? "", 128 ),
				["build"] = "",
				["uptimeSeconds"] = service.UptimeSeconds,
				// Protocol v1 (sboxlist.com) reads serverTimestamp; the v0.2 spec and the test backend read serverTime.
				// Both are sent, because each side ignores the fields it does not know (PROTOCOL.md section 3).
				["serverTime"] = stamp,
				["serverTimestamp"] = stamp,
				["plugin"] = "sboxlist/" + SboxlistService.Version,
			};

			var r = await SendAsync( "heartbeat", body );
			if ( r.Ok )
			{
				failures = 0;
				if ( !HeartbeatAccepted ) Log.Info( $"[sboxlist] {Config.SiteId}: heartbeat accepted" );
				HeartbeatAccepted = true;
				LastError = "";
				var next = JsonRead.Long( r.Json, "nextHeartbeatSeconds" ) ?? 60;
				nextHeartbeatAt = Now + Math.Clamp( next, 20, 3600 );
			}
			else if ( r.Status == 409 )
			{
				nextHeartbeatAt = Now + 60;
			}
			else
			{
				HeartbeatAccepted = false;
				nextHeartbeatAt = Now + Failed( r );
			}
		}
		finally
		{
			heartbeatBusy = false;
		}
	}

	// ---------------------------------------------------------------- poll and ack

	async Task PollAsync()
	{
		pollBusy = true;
		try
		{
			var r = await SendAsync( "votes/poll", new JsonObject { ["type"] = "poll" } );
			if ( !r.Ok )
			{
				nextPollAt = Now + Failed( r );
				return;
			}

			failures = 0;
			ClearErrorFrom( r.Route );
			if ( JsonRead.Get( r.Json, "messages" ) is JsonArray messages )
			{
				foreach ( var element in messages )
				{
					var message = JsonRead.Message( element, out var rawChars );
					if ( message is null ) continue;
					try { HandleMessage( message, rawChars ); }
					catch ( Exception e ) { Log.Warning( $"[sboxlist] {Config.SiteId}: a message could not be handled: {e.Message}" ); }
				}
			}

			var after = JsonRead.Long( r.Json, "pollAfterSeconds" ) ?? 15;
			nextPollAt = Now + Math.Clamp( after, 5, 300 );
		}
		finally
		{
			pollBusy = false;
		}
	}

	void HandleMessage( JsonNode message, int rawChars )
	{
		var id = JsonRead.Str( message, "id" );
		if ( !SboxlistConfig.IsIdent( id, 1, 64 ) ) return;

		var type = JsonRead.Str( message, "type" );
		if ( rawChars > 4096 || (message?.ToJsonString().Length ?? 0) > 4096 ) { Ack( id, false, "vote", "message too long" ); return; }
		if ( type != "vote" ) { Ack( id, false, "vote", "unsupported" ); return; }

		// Seen before (our ack was lost): ack again, do nothing else.
		if ( !Remember( dedupe.Votes, dedupe.VoteOrder, id ) ) { Ack( id, true, "vote" ); return; }

		var identity = JsonRead.Get( message, "identity" );
		var idKind = JsonRead.Str( identity, "kind" ) ?? "";
		var idKey = JsonRead.Str( identity, "key" ) ?? "";
		var isTest = JsonRead.Bool( message, "test" ) ?? id.StartsWith( "test-" );
		var online = isTest ? null : service.FindPlayer( idKind, idKey );

		var vote = new VoteInfo
		{
			SiteId = Config.SiteId,
			Id = id,
			IdentityKind = idKind,
			IdentityKey = idKey,
			At = JsonRead.Str( message, "at" ) ?? "",
			IsTest = isTest,
			OnlinePlayer = online,
		};

		VotesReceived++;
		LastVoteId = id;
		Log.Info( vote.IsTest
			? $"[sboxlist] {Config.SiteId}: test vote arrived ({id}), identity {vote.IdentityKind} {vote.IdentityKey}"
			: $"[sboxlist] {Config.SiteId}: vote {id} from {vote.IdentityKind} {vote.IdentityKey}" );

		SboxlistEvents.RaiseVote( vote );
		service.OnVoteArrived( this, vote );
		Ack( id, true, "vote" );

		// The voter is online right now: ask for the reward at once.
		if ( !vote.IsTest && Config.Rewards && online is not null )
			QueueJoin( online, "vote" );
	}

	internal void Ack( string id, bool ok, string kind, string error = null )
	{
		var item = new JsonObject { ["id"] = id, ["ok"] = ok, ["kind"] = kind };
		if ( error is not null ) item["error"] = Clip( error, 200 );
		pendingAcks.Add( item );
	}

	async Task FlushAcksAsync()
	{
		ackBusy = true;
		try
		{
			// One batch: up to ackLimit acks and about 12 KB, so the body stays under the site's limit.
			var batch = new List<JsonObject>();
			var size = 0;
			foreach ( var item in pendingAcks )
			{
				var length = Encoding.UTF8.GetByteCount( item.ToJsonString() ) + 1;
				if ( batch.Count == ackLimit || (batch.Count > 0 && size + length > MaxAckBatchBytes) ) break;
				batch.Add( item );
				size += length;
			}
			pendingAcks.RemoveRange( 0, batch.Count );

			// The request gets copies: a JSON node can have one parent only, and the originals go back if the send fails.
			var list = new JsonArray();
			foreach ( var item in batch ) list.Add( (JsonObject)JsonNode.Parse( item.ToJsonString() ) );

			var r = await SendAsync( "votes/ack", new JsonObject { ["type"] = "ack", ["acks"] = list } );
			if ( r.Ok )
			{
				ackLimit = 50;
				ClearErrorFrom( r.Route );
				return;
			}

			// The site refused several acks together: send them one at a time, so one bad item is dropped alone.
			if ( batch.Count > 1 && (r.Status == 400 || r.Status == 413) )
			{
				ackLimit = 1;
				pendingAcks.InsertRange( 0, batch );
				ackRetryAt = Now;
				Log.Warning( $"[sboxlist] {Config.SiteId}: the site refused {batch.Count} acks together (HTTP {r.Status}); sending them one by one" );
				return;
			}

			// The site refused this ack itself (a bug in what we sent, or an unknown field): sending it again changes nothing.
			// A vote whose ack never arrives is sent again by the site, and then it is acked again.
			if ( r.Status == 400 || r.Status == 405 || r.Status == 413 || r.Status == 415 )
			{
				Failed( r );
				Log.Warning( $"[sboxlist] {Config.SiteId}: {batch.Count} ack(s) dropped" );
				return;
			}

			// Network trouble, 429, 5xx, 401, 404 or 410: keep the acks and try again later, on the ack route's own timer.
			pendingAcks.InsertRange( 0, batch );
			ackRetryAt = Now + Failed( r );
		}
		finally
		{
			ackBusy = false;
		}
	}

	// ---------------------------------------------------------------- join and rewards

	async Task JoinAsync( JoinRequest join )
	{
		joinBusy = true;
		try
		{
			var player = join.Player;
			var steam = player.SteamId.ValueUnsigned;
			var steamText = steam == 0 ? null : steam.ToString( CultureInfo.InvariantCulture );
			if ( !IsIndividualSteamId( steamText ) ) steamText = null;

			// Protocol v1 name rule: 1 to 32 code points, no NUL, no unpaired surrogate. A name
			// outside it is not sent: the join carries the SteamID64 alone (the SteamID-only join), so a Steam vote is still paid,
			// and no name vote can match it. A player with neither a usable name nor a SteamID64 is not sent at all.
			// The name is sent exactly as the server reports it, with no trimming and no character removal.
			var name = player.DisplayName ?? "";
			var nameFits = FitsJoinName( name );
			if ( !nameFits && steamText is null )
			{
				Log.Warning( $"[sboxlist] {Config.SiteId}: a player with no usable name and no SteamID64 cannot be matched to a vote; no join sent" );
				return;
			}
			var who = nameFits ? name : steamText;

			var body = new JsonObject
			{
				["type"] = "join",
				["reason"] = join.Reason,
				["playerName"] = nameFits ? name : null,
				["steamId"] = steamText,
			};

			var r = await SendAsync( "rewards/join", body );
			if ( !r.Ok )
			{
				var wait = Failed( r );
				if ( Retryable( r.Status ) && join.Attempts + 1 < MaxJoinAttempts )
				{
					// A vote is acked before its reward is asked for, so the site will not send it again: the join is tried again.
					join.Attempts++;
					joinRetryAt = Now + wait;
					joins.Enqueue( join );
				}
				else
				{
					Log.Warning( $"[sboxlist] {Config.SiteId}: the reward request for {who} was given up after {join.Attempts + 1} attempt(s)" );
				}
				return;
			}

			ClearErrorFrom( r.Route );
			if ( JsonRead.Get( r.Json, "messages" ) is JsonArray rewards )
			{
				foreach ( var element in rewards )
				{
					var reward = JsonRead.Message( element, out var rawChars );
					if ( reward is null ) continue;
					try { RunReward( reward, player, rawChars ); }
					catch ( Exception e ) { Log.Warning( $"[sboxlist] {Config.SiteId}: a reward could not be run: {e.Message}" ); }
				}
			}
		}
		finally
		{
			joinBusy = false;
		}
	}

	void RunReward( JsonNode reward, Connection player, int rawChars )
	{
		var id = JsonRead.Str( reward, "id" );
		if ( !SboxlistConfig.IsIdent( id, 1, 64 ) )
		{
			// Without an id the library cannot ack the reward, so the site could never close it (PROTOCOL.md 6.6).
			Log.Warning( $"[sboxlist] {Config.SiteId}: a reward without a valid id was dropped; the site must send one (PROTOCOL.md 6.6)" );
			return;
		}

		if ( !Remember( dedupe.Rewards, dedupe.RewardOrder, id ) ) { Ack( id, true, "reward" ); return; }

		// Remembered above, so a run that fails is never run again: it is acked with the reason instead.
		string error;
		if ( rawChars > 4096 || (reward?.ToJsonString().Length ?? 0) > 4096 )
			error = "message too long";
		else if ( JsonRead.Str( reward, "type" ) is string rtype && rtype != "reward" )
			error = "unsupported";
		else
		{
			try { error = SboxlistRewards.Run( this, reward, player ); }
			catch ( Exception e ) { error = Clip( "reward failed: " + e.Message, 200 ); }
		}

		if ( error is null ) RewardsRun++;
		Ack( id, error is null, "reward", error );
	}

	// ---------------------------------------------------------------- operator commands

	public async Task RequestTestvoteAsync()
	{
		if ( testvoteBusy ) return;
		testvoteBusy = true;
		try
		{
			var r = await SendAsync( "votes/testvote", new JsonObject { ["type"] = "testvote" } );
			if ( r.Ok )
			{
				ClearErrorFrom( r.Route );
				Log.Info( $"[sboxlist] {Config.SiteId}: test vote {JsonRead.Str( r.Json, "id" )} queued; it arrives with the next poll" );
				nextPollAt = Math.Min( nextPollAt, Now + 1 );
			}
			else if ( r.Status == 404 )
			{
				// Not a stop: a site without a server-side test vote (sboxlist.com has none) just says so.
				Log.Info( $"[sboxlist] {Config.SiteId}: this site has no server-side test vote; use the site's own test vote" );
			}
			else
			{
				Failed( r );
				Log.Warning( $"[sboxlist] {Config.SiteId}: test vote refused: {LastError}" );
			}
		}
		finally
		{
			testvoteBusy = false;
		}
	}

	public async Task InfoAsync()
	{
		if ( infoBusy ) return;
		infoBusy = true;
		try
		{
			using var cts = new CancellationTokenSource( TimeSpan.FromSeconds( 10 ) );
			using var response = await Http.RequestAsync( Url( "info" ), "GET", null, null, cts.Token );
			if ( (int)response.StatusCode == 404 )
			{
				// Not a stop: a site without /v1/info (sboxlist.com has none) just is not clock-checked.
				Log.Info( $"[sboxlist] {Config.SiteId}: this site has no /v1/info; the clock is not checked" );
				return;
			}

			var text = await response.Content.ReadAsStringAsync();
			var json = JsonRead.Parse( text );
			var serverTime = JsonRead.Long( json, "serverTime" );
			if ( serverTime is long t ) ClockOffsetSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t;
			Log.Info( $"[sboxlist] {Config.SiteId}: info HTTP {(int)response.StatusCode}, site '{JsonRead.Str( json, "siteId" )}', clock difference {ClockOffsetSeconds?.ToString( "0" ) ?? "?"} s" );
			if ( ClockOffsetSeconds is double d && Math.Abs( d ) > 60 )
				Log.Warning( $"[sboxlist] {Config.SiteId}: this clock is {d:0} s off the site's; over 300 s every request is refused" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sboxlist] {Config.SiteId}: info failed: {e.Message}" );
		}
		finally
		{
			infoBusy = false;
		}
	}

	public string Describe()
	{
		var state = Disabled ? $"STOPPED ({DisabledReason})" : HeartbeatAccepted ? "online" : "not accepted yet";
		return $"{Config.SiteId} [{Config.Auth}] {state}; last {LastRoute} HTTP {LastStatus}; votes {VotesReceived}, rewards {RewardsRun}; acks waiting {pendingAcks.Count}, joins waiting {joins.Count}"
			+ (LastError.Length > 0 ? $"; last error: {LastError}" : "");
	}

	// ---------------------------------------------------------------- helpers

	static bool Remember( HashSet<string> set, Queue<string> order, string id )
	{
		if ( !set.Add( id ) ) return false;
		order.Enqueue( id );
		while ( order.Count > 1000 ) set.Remove( order.Dequeue() );
		return true;
	}

	/// <summary>Clips text to max characters without splitting surrogate pairs or keeping control/lone-surrogate characters.</summary>
	internal static string Clip( string value, int max )
	{
		if ( string.IsNullOrEmpty( value ) || max <= 0 ) return "";
		var sb = new StringBuilder( Math.Min( value.Length, max ) );
		for ( var i = 0; i < value.Length; i++ )
		{
			var c = value[i];
			if ( char.IsControl( c ) )
			{
				if ( sb.Length + 1 > max ) break;
				sb.Append( ' ' );
				continue;
			}
			if ( char.IsHighSurrogate( c ) && i + 1 < value.Length && char.IsLowSurrogate( value[i + 1] ) )
			{
				if ( sb.Length + 2 > max ) break;
				sb.Append( c ).Append( value[++i] );
				continue;
			}
			if ( char.IsSurrogate( c ) )
			{
				if ( sb.Length + 1 > max ) break;
				sb.Append( ' ' );
				continue;
			}
			if ( sb.Length + 1 > max ) break;
			sb.Append( c );
		}
		return sb.ToString();
	}

	/// <summary>Valve Universe 1 AccountType 1 Individual SteamID64 (sboxlist.com protocol v1 STEAM_ID: 7656119 + 10 digits).</summary>
	internal static bool IsIndividualSteamId( string value )
	{
		if ( value is null || value.Length != 17 || !value.StartsWith( "7656119" ) ) return false;
		foreach ( var c in value ) if ( c < '0' || c > '9' ) return false;
		return true;
	}

	/// Protocol v1 join name rule: 1 to 32 code points, no NUL and no unpaired surrogate.
	internal static bool FitsJoinName( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return false;
		var codePoints = 0;
		for ( var i = 0; i < name.Length; i++ )
		{
			var c = name[i];
			if ( c == '\0' ) return false;
			if ( char.IsHighSurrogate( c ) && i + 1 < name.Length && char.IsLowSurrogate( name[i + 1] ) ) i++;
			else if ( char.IsSurrogate( c ) ) return false;
			if ( ++codePoints > 32 ) return false;
		}
		return true;
	}
}
