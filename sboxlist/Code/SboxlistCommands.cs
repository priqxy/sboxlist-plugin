using System.Text;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>
/// Console commands for the owner (type them in the server console, or in the editor console
/// while playing). They act only on the host; on a client they say so and do nothing.
/// </summary>
public static class SboxlistCommands
{
	static SboxlistService HostService()
	{
		if ( !Networking.IsHost )
		{
			Log.Warning( "[sboxlist] run this on the server (host), not on a client" );
			return null;
		}

		var service = SboxlistService.Instance;
		if ( service is null ) Log.Warning( "[sboxlist] there is no SboxlistService component in the scene" );
		return service;
	}

	[ConCmd( "sbl_status" )]
	public static void Status()
	{
		var service = HostService();
		if ( service is null ) return;
		Log.Info( $"[sboxlist] {SboxlistService.Version}, {service.Sites.Count} site(s), config {SboxlistConfig.FullPath()}" );
		if ( service.VotePartyGoal > 0 )
			Log.Info( $"[sboxlist] vote party: {service.VotePartyProgress} / {service.VotePartyGoal} (total votes: {service.TotalVotesCounted})" );
		foreach ( var site in service.Sites ) Log.Info( "[sboxlist] " + site.Describe() );
	}

	[ConCmd( "sbl_vote" )]
	public static void VoteUrl()
	{
		var service = HostService();
		if ( service is null ) return;
		foreach ( var site in service.Sites )
			Log.Info( $"[sboxlist] {site.Config.SiteId} vote URL: {service.GetVoteUrl( site.Config.SiteId )}" );
	}

	[ConCmd( "sbl_info" )]
	public static void Info()
	{
		var service = HostService();
		if ( service is null ) return;
		foreach ( var site in service.Sites ) _ = site.InfoAsync();
	}

	[ConCmd( "sbl_heartbeat" )]
	public static void Heartbeat()
	{
		var service = HostService();
		if ( service is null ) return;
		foreach ( var site in service.Sites ) site.ForceHeartbeat();
		Log.Info( "[sboxlist] heartbeat on the next frame" );
	}

	[ConCmd( "sbl_testvote" )]
	public static void Testvote()
	{
		var service = HostService();
		if ( service is null ) return;
		foreach ( var site in service.Sites ) _ = site.RequestTestvoteAsync();
	}

	[ConCmd( "sbl_reload" )]
	public static void Reload()
	{
		var service = HostService();
		service?.Reload();
	}

	/// <summary>Verifies the HMAC-SHA256 implementation against fixed RFC 4231 and protocol v1 vectors offline.</summary>
	[ConCmd( "sbl_hmac_selftest" )]
	public static void HmacSelftest()
	{
		var rfc = SboxlistCrypto.Hex( SboxlistCrypto.HmacSha256( Encoding.UTF8.GetBytes( "Jefe" ), Encoding.UTF8.GetBytes( "what do ya want for nothing?" ) ) );
		var rfcOk = rfc == "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843";
		Log.Info( $"[sboxlist] RFC 4231 case 2: {rfc} {(rfcOk ? "PASS" : "FAIL")}" );

		var key = SboxlistCrypto.KeyFromToken( "sbs_" + new string( 'A', 43 ) );
		var body = Encoding.UTF8.GetBytes( "{\"v\":1,\"type\":\"heartbeat\"}" );
		var sig = SboxlistCrypto.Signature( key, "3f2a9c1e-5b7d-4e8a-9c0d-1a2b3c4d5e6f", "1790000000", "abcdefghijklmnop", body );
		var sigOk = sig == "62c1de08862566ae6cc2008bcef3a3bccb56c6e0e53897f48fc08883ae2cccfd";
		Log.Info( $"[sboxlist] protocol v1 vector: {sig} {(sigOk ? "PASS" : "FAIL")}" );
	}

	/// <summary>Safe built-in target for testing console-command rewards: logs the resolved SteamID64 and amount.</summary>
	[ConCmd( "sbl_test_reward" )]
	public static void TestReward( string steamId, int amount )
	{
		Log.Info( $"[sboxlist] sbl_test_reward ran: steamid {steamId}, amount {amount}" );
	}
}
