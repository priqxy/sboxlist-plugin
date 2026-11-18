using System;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>
/// Runs one reward message (PROTOCOL.md 6.6) for the player who joined. Returns null when done,
/// or the reason it was refused (that reason goes back to the site in the ack).
/// </summary>
public static class SboxlistRewards
{
	internal static string Run( SiteClient site, JsonNode reward, Connection player )
	{
		var kind = JsonRead.Str( reward, "kind" ) ?? "";
		var payload = JsonRead.Str( reward, "payload" ) ?? "";
		var amount = JsonRead.Long( reward, "amount" ) ?? 0;
		var identity = JsonRead.Get( reward, "identity" );
		var identityKind = JsonRead.Str( identity, "kind" ) ?? "";
		var identityKey = JsonRead.Str( identity, "key" ) ?? "";

		if ( amount < 1 || amount > 1_000_000 ) return "amount out of range";
		var payloadProblem = PayloadProblem( payload );
		if ( payloadProblem is not null ) return payloadProblem;

		switch ( kind )
		{
			case "console":
			{
				var command = ConsoleCommandForPlayer( payload, identityKind, identityKey, amount, player, out var problem );
				if ( command is null ) return problem;
				try
				{
					ConsoleSystem.Run( command );
					Log.Info( $"[sboxlist] {site.Config.SiteId}: reward ran: {command}" );
					return null;
				}
				catch ( Exception e )
				{
					return Clip( $"console: {e.Message}" );
				}
			}

			case "chat":
			case "toast":
			{
				var resolvedSteam = ResolveSteamId( player, identityKind, identityKey );
				var textProblem = TextProblem( payload, resolvedSteam is not null ? "steam" : identityKind, resolvedSteam ?? identityKey );
				if ( textProblem is not null ) return textProblem;
				var text = Text( payload, player, identityKind, identityKey, amount );
				SboxlistPlayerMessages.SendTo( player, kind, text );
				Log.Info( $"[sboxlist] {site.Config.SiteId}: {kind} to {player?.DisplayName}: {text}" );
				return null;
			}

			case "item":
			case "currency":
			{
				var info = new RewardInfo
				{
					SiteId = site.Config.SiteId,
					Id = JsonRead.Str( reward, "id" ),
					VoteId = JsonRead.Str( reward, "voteId" ),
					RuleId = JsonRead.Str( reward, "ruleId" ),
					Kind = kind,
					Payload = payload,
					Amount = amount,
					IdentityKind = identityKind,
					IdentityKey = identityKey,
					Player = player,
				};
				SboxlistEvents.DispatchReward( info );
				if ( !info.Handled ) return "the game has no RewardReceived handler";
				if ( info.Error is not null ) return Clip( info.Error );
				return null;
			}

			default:
				return "unsupported kind";
		}
	}

	/// <summary>
	/// Resolves console placeholders using the vote identity first, and falls back to the connected
	/// player's SteamID64 or display name when a name voter needs {steamid} or a Steam voter needs {name}.
	/// </summary>
	public static string ConsoleCommandForPlayer( string payload, string identityKind, string identityKey, long amount, Connection player, out string problem )
	{
		if ( payload is not null && player is not null )
		{
			var hasSteam = payload.IndexOf( "{steamid}", StringComparison.Ordinal ) >= 0;
			var hasName = payload.IndexOf( "{name}", StringComparison.Ordinal ) >= 0;
			var steamId = ResolveSteamId( player, identityKind, identityKey );
			var playerName = identityKind == "name" && !string.IsNullOrEmpty( identityKey )
				? identityKey
				: SboxlistPlayerMessages.ResolveName( player, identityKind, identityKey );

			if ( hasSteam && !hasName && identityKind != "steam" && steamId is not null )
				return ConsoleCommand( payload, "steam", steamId, amount, out problem );

			if ( hasName && !hasSteam && identityKind != "name" && !string.IsNullOrEmpty( playerName ) && !HasForbidden( playerName ) )
				return ConsoleCommand( payload, "name", playerName, amount, out problem );

			if ( hasSteam && hasName && steamId is not null && !string.IsNullOrEmpty( playerName ) && !HasForbidden( playerName ) )
				return ConsoleCommandBoth( payload, steamId, playerName, amount, out problem );
		}

		return ConsoleCommand( payload, identityKind, identityKey, amount, out problem );
	}

	/// <summary>
	/// One command, no ; quotes backquote backslash or control characters. {steamid} only for a
	/// Steam voter, {name} only for a name voter (put in double quotes), {amount}. Anything else: refused.
	/// </summary>
	public static string ConsoleCommand( string payload, string identityKind, string identityKey, long amount, out string problem )
	{
		problem = null;
		if ( string.IsNullOrWhiteSpace( payload ) ) { problem = "empty command"; return null; }
		if ( HasForbidden( payload ) ) { problem = "command holds ; quote backquote backslash or a control character"; return null; }

		var sb = new StringBuilder();
		var i = 0;
		while ( i < payload.Length )
		{
			var c = payload[i];
			if ( c == '}' ) { problem = "stray }"; return null; }
			if ( c != '{' ) { sb.Append( c ); i++; continue; }

			var end = payload.IndexOf( '}', i );
			if ( end < 0 ) { problem = "unclosed {"; return null; }
			var token = payload.Substring( i, end - i + 1 );

			switch ( token )
			{
				case "{amount}":
					sb.Append( amount.ToString( CultureInfo.InvariantCulture ) );
					break;
				case "{steamid}":
					if ( identityKind != "steam" ) { problem = "{steamid} needs a Steam voter"; return null; }
					if ( !IsDigits( identityKey, 17 ) ) { problem = "bad SteamID64"; return null; }
					sb.Append( identityKey );
					break;
				case "{name}":
					if ( identityKind != "name" ) { problem = "{name} needs a name voter"; return null; }
					if ( string.IsNullOrEmpty( identityKey ) || HasForbidden( identityKey ) ) { problem = "the name holds a character a command may not"; return null; }
					sb.Append( '"' ).Append( identityKey ).Append( '"' );
					break;
				default:
					problem = $"unknown placeholder {token}";
					return null;
			}

			i = end + 1;
		}

		return sb.ToString().Trim();
	}

	static string ConsoleCommandBoth( string payload, string steamId, string playerName, long amount, out string problem )
	{
		problem = null;
		if ( string.IsNullOrWhiteSpace( payload ) ) { problem = "empty command"; return null; }
		if ( HasForbidden( payload ) ) { problem = "command holds ; quote backquote backslash or a control character"; return null; }

		var sb = new StringBuilder();
		var i = 0;
		while ( i < payload.Length )
		{
			var c = payload[i];
			if ( c == '}' ) { problem = "stray }"; return null; }
			if ( c != '{' ) { sb.Append( c ); i++; continue; }

			var end = payload.IndexOf( '}', i );
			if ( end < 0 ) { problem = "unclosed {"; return null; }
			var token = payload.Substring( i, end - i + 1 );

			switch ( token )
			{
				case "{amount}":
					sb.Append( amount.ToString( CultureInfo.InvariantCulture ) );
					break;
				case "{steamid}":
					sb.Append( steamId );
					break;
				case "{name}":
					sb.Append( '"' ).Append( playerName ).Append( '"' );
					break;
				default:
					problem = $"unknown placeholder {token}";
					return null;
			}

			i = end + 1;
		}

		return sb.ToString().Trim();
	}

	static string ResolveSteamId( Connection player, string identityKind, string identityKey )
	{
		if ( identityKind == "steam" && IsDigits( identityKey, 17 ) ) return identityKey;
		if ( player is not null && player.SteamId.ValueUnsigned != 0 )
		{
			var s = player.SteamId.ValueUnsigned.ToString( CultureInfo.InvariantCulture );
			if ( SiteClient.IsIndividualSteamId( s ) ) return s;
		}
		return null;
	}

	/// <summary>Chat and toast text: {name} is the player's resolved name, {steamid} and {amount} as given.</summary>
	static string Text( string payload, Connection player, string identityKind, string identityKey, long amount )
	{
		var name = SboxlistPlayerMessages.ResolveName( player, identityKind, identityKey );
		var steam = ResolveSteamId( player, identityKind, identityKey ) ?? "";
		return payload
			.Replace( "{name}", name )
			.Replace( "{steamid}", steam )
			.Replace( "{amount}", amount.ToString( CultureInfo.InvariantCulture ) );
	}

	/// <summary>Chat and toast text: only {name}, {steamid} and {amount}; {steamid} needs a valid SteamID64. Anything else is refused (PROTOCOL.md 6.6).</summary>
	static string TextProblem( string payload, string identityKind, string identityKey )
	{
		var i = 0;
		while ( i < payload.Length )
		{
			var c = payload[i];
			if ( c == '}' ) return "stray }";
			if ( c != '{' ) { i++; continue; }

			var end = payload.IndexOf( '}', i );
			if ( end < 0 ) return "unclosed {";
			var token = payload.Substring( i, end - i + 1 );
			switch ( token )
			{
				case "{amount}":
				case "{name}":
					break;
				case "{steamid}":
					if ( identityKind != "steam" || !IsDigits( identityKey, 17 ) ) return "{steamid} needs a Steam voter";
					break;
				default:
					return $"unknown placeholder {token}";
			}

			i = end + 1;
		}

		return null;
	}

	/// <summary>PROTOCOL.md 6.6: at most 200 code points, no control character and no lone surrogate.</summary>
	internal static string PayloadProblem( string s )
	{
		if ( s is null ) return "empty payload";
		var codePoints = 0;
		for ( var i = 0; i < s.Length; i++ )
		{
			var c = s[i];
			if ( char.IsControl( c ) ) return "payload holds a control character";
			if ( char.IsHighSurrogate( c ) && i + 1 < s.Length && char.IsLowSurrogate( s[i + 1] ) ) i++;
			else if ( char.IsSurrogate( c ) ) return "payload holds a lone surrogate";
			if ( ++codePoints > 200 ) return "payload too long";
		}
		return null;
	}

	static bool HasForbidden( string s )
	{
		for ( var i = 0; i < s.Length; i++ )
		{
			var c = s[i];
			if ( c == ';' || c == '"' || c == '\'' || c == '`' || c == '\\' || char.IsControl( c ) ) return true;
			if ( char.IsHighSurrogate( c ) && i + 1 < s.Length && char.IsLowSurrogate( s[i + 1] ) ) { i++; continue; }
			if ( char.IsSurrogate( c ) ) return true;
		}
		return false;
	}

	static bool IsDigits( string s, int length )
	{
		if ( s is null || s.Length != length ) return false;
		foreach ( var c in s ) if ( c < '0' || c > '9' ) return false;
		return true;
	}

	static string Clip( string s ) => SiteClient.Clip( s, 200 );
}

/// <summary>
/// Messages on one player's screen. The host calls SendTo; the RPC runs only on that player's
/// machine (Rpc.FilterInclude), and only the host may call it (NetFlags.HostOnly), so a client
/// cannot fake a reward message to other players.
/// </summary>
public static class SboxlistPlayerMessages
{
	/// <summary>
	/// Optional callback for Roleplay / DarkRP gamemodes to return a player's active character name
	/// instead of their Steam DisplayName in chat, toast and broadcast messages.
	/// </summary>
	public static Func<Connection, string> CustomNameResolver { get; set; }

	/// <summary>Resolves the display name for a player using CustomNameResolver if set, falling back to DisplayName or the voter's identity key.</summary>
	public static string ResolveName( Connection player, string identityKind = "", string identityKey = "" )
	{
		if ( player is not null && CustomNameResolver is not null )
		{
			try
			{
				var custom = CustomNameResolver( player );
				if ( !string.IsNullOrWhiteSpace( custom ) ) return SiteClient.Clip( custom.Trim(), 64 );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[sboxlist] CustomNameResolver threw: {e.Message}" );
			}
		}

		if ( player is not null && !string.IsNullOrEmpty( player.DisplayName ) ) return player.DisplayName;
		if ( identityKind == "name" ) return identityKey ?? "";
		return !string.IsNullOrEmpty( identityKey ) ? identityKey : "";
	}

	/// <summary>Sends a reward chat or toast message from the host to one specific player.</summary>
	public static void SendTo( Connection player, string kind, string text )
	{
		if ( !Networking.IsHost || player is null ) return;
		using ( Rpc.FilterInclude( player ) )
		{
			Show( kind, text );
		}
	}

	/// <summary>Broadcasts a chat message from the host to all connected players (e.g. "Player voted!").</summary>
	public static void BroadcastChat( string text )
	{
		if ( !Networking.IsHost || string.IsNullOrEmpty( text ) ) return;
		Show( "broadcast_chat", SiteClient.Clip( text, 300 ) );
	}

	/// <summary>Broadcasts a toast notification from the host to all connected players.</summary>
	public static void BroadcastToast( string text )
	{
		if ( !Networking.IsHost || string.IsNullOrEmpty( text ) ) return;
		Show( "broadcast_toast", SiteClient.Clip( text, 300 ) );
	}

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.HostOnly )]
	public static void Show( string kind, string text )
	{
		var isToast = kind == "toast" || kind == "broadcast_toast";
		var isBroadcast = kind == "broadcast_chat" || kind == "broadcast_toast";
		var notification = new VoteNotification
		{
			Kind = isToast ? "toast" : "chat",
			IsBroadcast = isBroadcast,
			Text = text,
		};

		SboxlistEvents.DispatchNotification( notification );
		if ( isToast ) SboxlistEvents.RaiseToast( text );

		if ( notification.Handled ) return;

		if ( isToast )
			Sandbox.Platform.Chat.AddText( "★ " + text );
		else
			Sandbox.Platform.Chat.AddText( text );
	}
}
