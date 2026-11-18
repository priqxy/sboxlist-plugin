using System.Text.Json.Nodes;

namespace Sboxlist;

/// <summary>Reads fields from untrusted JSON without throwing. A wrong type reads as null.</summary>
internal static class JsonRead
{
	public static string Str( JsonNode node, string key )
	{
		try { return node?[key]?.GetValue<string>(); }
		catch { return null; }
	}

	public static long? Long( JsonNode node, string key )
	{
		try
		{
			var value = node?[key];
			if ( value is null ) return null;
			return value.GetValue<long>();
		}
		catch { return null; }
	}

	public static bool? Bool( JsonNode node, string key )
	{
		try
		{
			var value = node?[key];
			if ( value is null ) return null;
			return value.GetValue<bool>();
		}
		catch { return null; }
	}

	public static JsonNode Get( JsonNode node, string key )
	{
		try { return node?[key]; }
		catch { return null; }
	}

	public static JsonNode Parse( string text )
	{
		try { return string.IsNullOrEmpty( text ) ? null : JsonNode.Parse( text ); }
		catch { return null; }
	}

	/// <summary>
	/// One message of a poll or join answer. Protocol v1 (sboxlist.com) sends each message as JSON
	/// text, the v0.2 spec sends an object. Returns the object, or null for anything else.
	/// </summary>
	public static JsonNode Message( JsonNode element ) => Message( element, out _ );

	public static JsonNode Message( JsonNode element, out int rawChars )
	{
		rawChars = 0;
		if ( element is null ) return null;
		if ( element is JsonObject )
		{
			rawChars = element.ToJsonString().Length;
			return element;
		}
		try
		{
			var text = element.GetValue<string>();
			if ( text is null ) return null;
			rawChars = text.Length;
			return JsonNode.Parse( text ) as JsonObject;
		}
		catch { return null; }
	}
}
