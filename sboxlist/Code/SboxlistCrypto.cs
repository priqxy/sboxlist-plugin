using System;
using System.Security.Cryptography;
using System.Text;

namespace Sboxlist;

/// <summary>
/// Hashing for the protocol. Only SHA256 is on the s&amp;box whitelist (HMACSHA256 and
/// RandomNumberGenerator are not), so HMAC is built by hand (RFC 2104) and nonces come from Guid.
/// </summary>
public static class SboxlistCrypto
{
	public static string Hex( byte[] bytes ) => Convert.ToHexString( bytes ).ToLowerInvariant();

	public static byte[] Sha256( byte[] data ) => SHA256.HashData( data );

	public static string Sha256Hex( byte[] data ) => Hex( Sha256( data ) );

	/// <summary>HMAC-SHA256 from SHA256 alone: H((K0 ^ opad) || H((K0 ^ ipad) || message)).</summary>
	public static byte[] HmacSha256( byte[] key, byte[] message )
	{
		const int block = 64;
		var k = key.Length > block ? Sha256( key ) : key;

		var inner = new byte[block + message.Length];
		var outer = new byte[block + 32];

		for ( var i = 0; i < block; i++ )
		{
			var b = i < k.Length ? k[i] : (byte)0;
			inner[i] = (byte)(b ^ 0x36);
			outer[i] = (byte)(b ^ 0x5c);
		}

		for ( var i = 0; i < message.Length; i++ )
			inner[block + i] = message[i];

		var innerHash = Sha256( inner );
		for ( var i = 0; i < 32; i++ )
			outer[block + i] = innerHash[i];

		return Sha256( outer );
	}

	/// <summary>A nonce only has to be unique: 32 hex characters from a new Guid.</summary>
	public static string NewNonce() => Guid.NewGuid().ToString( "N" );

	/// <summary>Derives the 32-byte HMAC key from the server token: sha256(UTF-8(token)).</summary>
	public static byte[] KeyFromToken( string token ) => Sha256( Encoding.UTF8.GetBytes( token ) );

	/// <summary>Computes the hex HMAC-SHA256 signature over "sbl1\nserverId\ntimestamp\nnonce\nhex(sha256(body))".</summary>
	public static string Signature( byte[] key, string serverId, string timestamp, string nonce, byte[] body )
	{
		var canonical = "sbl1\n" + serverId + "\n" + timestamp + "\n" + nonce + "\n" + Sha256Hex( body );
		return Hex( HmacSha256( key, Encoding.UTF8.GetBytes( canonical ) ) );
	}
}
