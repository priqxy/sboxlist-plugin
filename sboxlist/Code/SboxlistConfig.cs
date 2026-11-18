using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>One server list this server is listed on (PROTOCOL.md section 7).</summary>
public sealed class SiteConfig
{
	public string SiteId { get; set; } = "";
	public string BaseUrl { get; set; } = "";
	public string ServerId { get; set; } = "";
	public string Token { get; set; } = "";
	/// <summary>hmac for sboxlist.com (protocol v1 accepts hmac only); bearer only for a site that accepts it.</summary>
	public string Auth { get; set; } = "hmac";
	public bool Rewards { get; set; } = true;
}

/// <summary>
/// Reads the site list. First choice: data/&lt;org&gt;/&lt;package&gt;/sboxlist.json on the server's own
/// disk (FileSystem.Data). Fallback for one site: the hidden ConVars below, set from the start
/// script (+sbl_base_url ... +sbl_server_id ... +sbl_token ...). The token is never put in a
/// prefab, a scene or a published package.
/// </summary>
public static class SboxlistConfig
{
	public const string FileName = "sboxlist.json";

	[ConVar( "sbl_base_url", ConVarFlags.Hidden )]
	public static string ConVarBaseUrl { get; set; } = "";

	[ConVar( "sbl_server_id", ConVarFlags.Hidden )]
	public static string ConVarServerId { get; set; } = "";

	[ConVar( "sbl_token", ConVarFlags.Hidden )]
	public static string ConVarToken { get; set; } = "";

	[ConVar( "sbl_auth", ConVarFlags.Hidden )]
	public static string ConVarAuth { get; set; } = "hmac";

	/// <summary>Logs every request and answer (never the token).</summary>
	[ConVar( "sbl_debug" )]
	public static bool Debug { get; set; } = false;

	public static string FullPath()
	{
		try { return FileSystem.Data.GetFullPath( FileName ); }
		catch ( Exception e ) { return $"(unknown: {e.Message})"; }
	}

	public static List<SiteConfig> Load( out string source )
	{
		var sites = new List<SiteConfig>();

		if ( FileSystem.Data.FileExists( FileName ) )
		{
			source = FullPath();
			JsonNode root;
			try
			{
				root = JsonNode.Parse( FileSystem.Data.ReadAllText( FileName ) );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[sboxlist] {source} is not valid JSON: {e.Message}" );
				return sites;
			}

			if ( root?["sites"] is JsonArray list )
			{
				foreach ( var item in list )
				{
					var site = new SiteConfig
					{
						SiteId = JsonRead.Str( item, "siteId" ) ?? "",
						BaseUrl = JsonRead.Str( item, "baseUrl" ) ?? "",
						ServerId = JsonRead.Str( item, "serverId" ) ?? "",
						Token = JsonRead.Str( item, "token" ) ?? "",
						Auth = JsonRead.Str( item, "auth" ) ?? "hmac",
						Rewards = JsonRead.Bool( item, "rewards" ) ?? true,
					};
					AddIfValid( sites, site );
				}
			}
			else
			{
				Log.Warning( $"[sboxlist] {source} has no \"sites\" list" );
			}

			if ( JsonRead.Bool( root, "debug" ) == true ) Debug = true;
			return sites;
		}

		if ( !string.IsNullOrWhiteSpace( ConVarBaseUrl ) )
		{
			source = "ConVars sbl_base_url / sbl_server_id / sbl_token";
			AddIfValid( sites, new SiteConfig
			{
				SiteId = HostOf( ConVarBaseUrl ),
				BaseUrl = ConVarBaseUrl.Trim(),
				ServerId = ConVarServerId.Trim(),
				Token = ConVarToken.Trim(),
				Auth = string.IsNullOrWhiteSpace( ConVarAuth ) ? "hmac" : ConVarAuth.Trim(),
				Rewards = true,
			} );
			return sites;
		}

		source = $"nothing found (looked for {FullPath()} and the sbl_* ConVars)";
		return sites;
	}

	static void AddIfValid( List<SiteConfig> sites, SiteConfig site )
	{
		site.BaseUrl = site.BaseUrl.Trim().TrimEnd( '/' );
		site.Auth = site.Auth.Trim().ToLowerInvariant();
		if ( string.IsNullOrWhiteSpace( site.SiteId ) ) site.SiteId = HostOf( site.BaseUrl );

		var problem = Problem( site );
		if ( problem is not null )
		{
			Log.Warning( $"[sboxlist] site '{site.SiteId}' skipped: {problem}" );
			return;
		}

		var warning = LocalhostWarning( site.BaseUrl );
		if ( warning is not null ) Log.Warning( $"[sboxlist] site '{site.SiteId}': {warning}" );

		foreach ( var existing in sites )
		{
			if ( existing.SiteId == site.SiteId )
			{
				Log.Warning( $"[sboxlist] site '{site.SiteId}' is listed twice; the second one is skipped" );
				return;
			}
		}

		sites.Add( site );
	}

	/// <summary>The reason a site is refused, or null. The site checks what it receives; this checks what the plugin is given.</summary>
	static string Problem( SiteConfig s )
	{
		var url = BaseUrlProblem( s.BaseUrl );
		if ( url is not null ) return url;
		if ( !IsSiteId( s.SiteId ) ) return "siteId must be lowercase letters, digits, dots and dashes, starting with a letter or digit";
		if ( !IsIdent( s.ServerId, 1, 64 ) ) return "serverId must be 1-64 of A-Z a-z 0-9 _ -";
		if ( !IsToken( s.Token ) ) return "token must be sbs_ or sbt_ followed by 43 characters";
		if ( s.Auth != "bearer" && s.Auth != "hmac" ) return "auth must be hmac or bearer";
		return null;
	}

	/// <summary>
	/// https with a domain name (s&amp;box refuses IP addresses), or http on localhost for tests.
	/// The port of a localhost address is only a warning, see LocalhostWarning.
	/// </summary>
	internal static string BaseUrlProblem( string url )
	{
		if ( string.IsNullOrEmpty( url ) ) return "baseUrl must be a full URL";
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var uri ) ) return "baseUrl must be a full URL";
		if ( uri.Scheme == "https" )
			return IsDomainName( uri.Host ) ? null : "baseUrl must name a domain, not an IP address";
		if ( uri.Scheme == "http" && uri.Host == "localhost" )
			return null;
		return "baseUrl must start with https:// and name a domain (http is allowed for localhost only, for tests)";
	}

	/// <summary>s&amp;box allows localhost only on ports 80, 443, 8080 and 8443, unless the editor or server runs with -allowlocalhttp.</summary>
	internal static string LocalhostWarning( string url )
	{
		if ( !Uri.TryCreate( url, UriKind.Absolute, out var uri ) || uri.Scheme != "http" || uri.Host != "localhost" ) return null;
		if ( uri.Port == 80 || uri.Port == 443 || uri.Port == 8080 || uri.Port == 8443 ) return null;
		return $"s&box refuses localhost port {uri.Port} unless the editor or server runs with -allowlocalhttp";
	}

	/// <summary>A domain name has a dot, and is not an IPv4 address (digits and dots only) or an IPv6 literal.</summary>
	static bool IsDomainName( string host )
	{
		if ( string.IsNullOrEmpty( host ) || host.Length > 253 || host.IndexOf( '.' ) < 0 ) return false;
		var letters = false;
		foreach ( var c in host )
		{
			if ( c == ':' || c == '[' || c == ']' ) return false;
			if ( !(char.IsDigit( c ) || c == '.') ) letters = true;
		}
		return letters;
	}

	/// <summary>sbs_ is the protocol v1 server secret prefix; sbt_ is the generic protocol token prefix (PROTOCOL.md section 4).</summary>
	internal static bool IsToken( string token )
	{
		if ( token is null || token.Length != 47 ) return false;
		if ( !(token.StartsWith( "sbs_" ) || token.StartsWith( "sbt_" )) ) return false;
		return IsIdent( token.Substring( 4 ), 43, 43 );
	}

	/// <summary>A site id: lowercase letters, digits, dots and dashes; at most 63 characters; starts with a letter or digit.</summary>
	internal static bool IsSiteId( string value )
	{
		if ( string.IsNullOrEmpty( value ) || value.Length > 63 ) return false;
		for ( var i = 0; i < value.Length; i++ )
		{
			var c = value[i];
			var letterOrDigit = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
			if ( i == 0 ? !letterOrDigit : !(letterOrDigit || c == '.' || c == '-') ) return false;
		}
		return true;
	}

	internal static bool IsIdent( string value, int min, int max )
	{
		if ( value is null || value.Length < min || value.Length > max ) return false;
		foreach ( var c in value )
		{
			var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
			if ( !ok ) return false;
		}
		return true;
	}

	static string HostOf( string url )
	{
		try { return new Uri( url.Trim() ).Host.ToLowerInvariant(); }
		catch { return "site"; }
	}
}
