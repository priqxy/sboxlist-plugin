using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>
/// Put one of these in the scene the server loads (next to the NetworkHelper). It runs only on
/// the host: it reads the config, sends heartbeats, polls for votes and asks for rewards when a
/// player joins. Clients do nothing.
/// </summary>
public sealed class SboxlistService : Component, Component.INetworkListener
{
	public const string Version = "1.0.0";

	public static SboxlistService Instance { get; private set; }

	public IReadOnlyList<SiteClient> Sites => sites;

	/// <summary>When true, broadcasts a message to all players whenever a non-test vote arrives.</summary>
	[Property] public bool AnnounceVotesInChat { get; set; } = false;

	/// <summary>When true (and AnnounceVotesInChat is true), broadcasts the vote announcement as a toast banner instead of a plain chat line.</summary>
	[Property] public bool AnnounceVotesAsToast { get; set; } = false;

	/// <summary>Template for server-wide vote announcements. Placeholders: {name}, {site}, {votes}, {goal}.</summary>
	[Property] public string VoteAnnouncementFormat { get; set; } = "★ {name} voted on {site}! Type !vote to claim your reward.";

	/// <summary>When greater than 0, triggers a server-wide Vote Party every N real votes.</summary>
	[Property] public int VotePartyGoal { get; set; } = 0;

	/// <summary>Announcement broadcast to all players when VotePartyGoal is reached. Placeholders: {goal}, {total}, {site}.</summary>
	[Property] public string VotePartyAnnouncement { get; set; } = "★ VOTE PARTY! ({goal}/{goal} votes reached) Everyone online gets a bonus!";

	/// <summary>Optional console command run once for each connected player when a Vote Party triggers (supports {steamid}, {name} and {amount}).</summary>
	[Property] public string VotePartyConsoleCommand { get; set; } = "";

	/// <summary>Current vote count toward the next Vote Party (0..VotePartyGoal).</summary>
	public int VotePartyProgress { get; private set; }

	/// <summary>Total non-test votes counted by this service instance since startup.</summary>
	public int TotalVotesCounted { get; private set; }

	readonly List<SiteClient> sites = new();
	double startedAt;
	bool loaded;

	protected override void OnStart()
	{
		Instance = this;
		startedAt = RealTime.NowDouble;
	}

	protected override void OnUpdate()
	{
		if ( !Networking.IsHost ) return;

		if ( !loaded )
		{
			loaded = true;
			Reload();
		}

		foreach ( var site in sites )
			site.Tick();
	}

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
	}

	public void Reload()
	{
		var previous = new List<SiteClient>( sites );
		sites.Clear();
		var configs = SboxlistConfig.Load( out var source );
		Log.Info( $"[sboxlist] {Version} config: {source}" );
		Log.Info( $"[sboxlist] config file path: {SboxlistConfig.FullPath()}" );

		foreach ( var config in configs )
		{
			// A running site with the same identity keeps its state (pending acks and, through its server id, what it
			// has seen). Only the rewards setting can change in place. A stopped site starts again.
			var kept = previous.FirstOrDefault( s => !s.Disabled && SameSite( s.Config, config ) );
			if ( kept is not null )
			{
				kept.Config.Rewards = config.Rewards;
				sites.Add( kept );
				continue;
			}

			var site = new SiteClient( config, this );
			sites.Add( site );
			Log.Info( $"[sboxlist] site {config.SiteId}: {config.BaseUrl} as {config.ServerId}, auth {config.Auth}, rewards {(config.Rewards ? "on" : "off")}" );
			_ = site.InfoAsync();
		}

		if ( sites.Count == 0 )
			Log.Warning( "[sboxlist] no site configured; nothing will be sent" );
	}

	static bool SameSite( SiteConfig a, SiteConfig b ) =>
		a.SiteId == b.SiteId && a.BaseUrl == b.BaseUrl && a.ServerId == b.ServerId && a.Token == b.Token && a.Auth == b.Auth;

	/// <summary>Called on the host when a player is fully in the game.</summary>
	public void OnActive( Connection channel )
	{
		if ( !Networking.IsHost || channel is null ) return;
		if ( Application.IsDedicatedServer && channel.IsHost ) return;
		foreach ( var site in sites )
			site.QueueJoin( channel, "join" );
	}

	/// <summary>Returns the public voting page URL for the first (or named) configured site.</summary>
	public string GetVoteUrl( string siteId = null )
	{
		var site = string.IsNullOrEmpty( siteId )
			? sites.FirstOrDefault()
			: sites.FirstOrDefault( s => s.Config.SiteId == siteId );
		if ( site is null ) return "";
		var baseRoot = site.Config.BaseUrl;
		if ( baseRoot.EndsWith( "/api", StringComparison.OrdinalIgnoreCase ) )
			baseRoot = baseRoot.Substring( 0, baseRoot.Length - 4 );
		return $"{baseRoot}/server/{site.Config.ServerId}/vote";
	}

	/// <summary>Sends a chat message with the server's direct voting URL to one player (call from your !vote or /vote chat handler).</summary>
	public void SendVoteLink( Connection player, string siteId = null )
	{
		if ( !Networking.IsHost || player is null ) return;
		var url = GetVoteUrl( siteId );
		if ( string.IsNullOrEmpty( url ) ) return;
		SboxlistPlayerMessages.SendTo( player, "chat", $"★ Vote for this server and claim your reward: {url}" );
	}

	internal void OnVoteArrived( SiteClient site, VoteInfo vote )
	{
		if ( vote is null || vote.IsTest ) return;

		TotalVotesCounted++;
		if ( VotePartyGoal > 0 ) VotePartyProgress++;

		var who = SboxlistPlayerMessages.ResolveName( vote.OnlinePlayer, vote.IdentityKind, vote.IdentityKey );
		if ( string.IsNullOrEmpty( who ) ) who = "A player";

		if ( AnnounceVotesInChat && !string.IsNullOrWhiteSpace( VoteAnnouncementFormat ) )
		{
			var msg = VoteAnnouncementFormat
				.Replace( "{name}", who )
				.Replace( "{site}", site.Config.SiteId )
				.Replace( "{votes}", VotePartyProgress.ToString( CultureInfo.InvariantCulture ) )
				.Replace( "{goal}", VotePartyGoal.ToString( CultureInfo.InvariantCulture ) );

			if ( AnnounceVotesAsToast ) SboxlistPlayerMessages.BroadcastToast( msg );
			else SboxlistPlayerMessages.BroadcastChat( msg );
		}

		if ( VotePartyGoal > 0 && VotePartyProgress >= VotePartyGoal )
		{
			var target = VotePartyGoal;
			VotePartyProgress = 0;

			if ( !string.IsNullOrWhiteSpace( VotePartyAnnouncement ) )
			{
				var partyMsg = VotePartyAnnouncement
					.Replace( "{goal}", target.ToString( CultureInfo.InvariantCulture ) )
					.Replace( "{total}", TotalVotesCounted.ToString( CultureInfo.InvariantCulture ) )
					.Replace( "{site}", site.Config.SiteId )
					.Replace( "{name}", who );

				if ( AnnounceVotesAsToast ) SboxlistPlayerMessages.BroadcastToast( partyMsg );
				else SboxlistPlayerMessages.BroadcastChat( partyMsg );
			}

			if ( !string.IsNullOrWhiteSpace( VotePartyConsoleCommand ) && Connection.All is not null )
			{
				foreach ( var c in Connection.All )
				{
					if ( Application.IsDedicatedServer && c.IsHost ) continue;
					var steamStr = c.SteamId.ValueUnsigned != 0 ? c.SteamId.ValueUnsigned.ToString( CultureInfo.InvariantCulture ) : "";
					var cmd = SboxlistRewards.ConsoleCommandForPlayer( VotePartyConsoleCommand, "steam", steamStr, 1, c, out var problem );
					if ( cmd is not null )
					{
						try { ConsoleSystem.Run( cmd ); }
						catch ( Exception e ) { Log.Warning( $"[sboxlist] VotePartyConsoleCommand failed: {e.Message}" ); }
					}
					else if ( problem is not null )
					{
						Log.Warning( $"[sboxlist] VotePartyConsoleCommand skipped for {c.DisplayName}: {problem}" );
					}
				}
			}

			SboxlistEvents.RaiseVoteParty( new VotePartyInfo
			{
				SiteId = site.Config.SiteId,
				TargetVotes = target,
				TotalVotes = TotalVotesCounted,
				TriggeringVote = vote,
			} );
		}
	}

	internal int PlayerCount()
	{
		var all = Connection.All;
		if ( all is null ) return 0;
		return Application.IsDedicatedServer ? all.Count( c => !c.IsHost ) : all.Count;
	}

	internal string MapName => Scene?.Name ?? "";

	internal long UptimeSeconds => (long)Math.Max( 0, RealTime.NowDouble - startedAt );

	/// <summary>The connected player a vote belongs to, by SteamID64 or by exact name.</summary>
	internal Connection FindPlayer( string identityKind, string identityKey )
	{
		var all = Connection.All;
		if ( all is null || string.IsNullOrEmpty( identityKey ) ) return null;

		foreach ( var c in all )
		{
			if ( Application.IsDedicatedServer && c.IsHost ) continue;
			if ( identityKind == "steam" && c.SteamId.ValueUnsigned.ToString( CultureInfo.InvariantCulture ) == identityKey ) return c;
			if ( identityKind == "name" && c.DisplayName == identityKey ) return c;
		}

		return null;
	}
}
