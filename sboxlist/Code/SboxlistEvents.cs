using System;
using System.Collections.Generic;
using Sandbox;
using static Sandbox.Internal.GlobalGameNamespace;

namespace Sboxlist;

/// <summary>A vote that arrived from a site (on the host).</summary>
public sealed class VoteInfo
{
	public string SiteId { get; init; }
	public string Id { get; init; }
	/// <summary>"steam" or "name".</summary>
	public string IdentityKind { get; init; }
	/// <summary>A SteamID64 (as text) or the name the voter typed.</summary>
	public string IdentityKey { get; init; }
	public string At { get; init; }
	public bool IsTest { get; init; }
	/// <summary>The voter's active Connection if they are online right now; null when offline or for a test vote.</summary>
	public Connection OnlinePlayer { get; init; }
}

/// <summary>
/// An item or currency reward for the game to hand out on the host (ISboxlistListener.OnVoteReward
/// or SboxlistEvents.RewardReceived). Call Refuse(reason) if the reward cannot be granted so the
/// site records ok:false with the reason in the owner's reward log.
/// </summary>
public sealed class RewardInfo
{
	public string SiteId { get; init; }
	public string Id { get; init; }
	public string VoteId { get; init; }
	public string RuleId { get; init; }
	/// <summary>"item" or "currency".</summary>
	public string Kind { get; init; }
	public string Payload { get; init; }
	public long Amount { get; init; }
	/// <summary>"steam" or "name".</summary>
	public string IdentityKind { get; init; }
	/// <summary>A SteamID64 (as text) or the name the voter typed.</summary>
	public string IdentityKey { get; init; }
	/// <summary>The player who joined and gets the reward.</summary>
	public Connection Player { get; init; }
	/// <summary>Set to true by your listener when handled (automatically set for RewardReceived subscribers).</summary>
	public bool Handled { get; set; }
	/// <summary>Non-null when the game refused the reward via Refuse(reason).</summary>
	public string Error { get; private set; }

	/// <summary>Marks this reward as refused; the library sends ok:false with this reason in votes/ack.</summary>
	public void Refuse( string reason )
	{
		Handled = true;
		Error = string.IsNullOrWhiteSpace( reason ) ? "refused by game" : reason.Trim();
	}
}

/// <summary>Fired on the host when SboxlistService's VotePartyGoal counter reaches its target.</summary>
public sealed class VotePartyInfo
{
	public string SiteId { get; init; }
	public int TargetVotes { get; init; }
	public int TotalVotes { get; init; }
	public VoteInfo TriggeringVote { get; init; }
}

/// <summary>
/// A chat, toast or broadcast message delivered to a client (ISboxlistNotificationListener or
/// SboxlistEvents.NotificationReceived). Set Handled = true in your custom UI to suppress the
/// default Sandbox.Platform.Chat fallback.
/// </summary>
public sealed class VoteNotification
{
	/// <summary>"chat" or "toast".</summary>
	public string Kind { get; init; }
	/// <summary>True when sent to everyone via BroadcastChat / BroadcastToast; false when sent to one voter.</summary>
	public bool IsBroadcast { get; init; }
	/// <summary>Formatted message text with {name}, {steamid} and {amount} already replaced.</summary>
	public string Text { get; init; }
	/// <summary>Set to true to suppress the default Platform.Chat line.</summary>
	public bool Handled { get; set; }
}

/// <summary>
/// Scene event interface for host-side vote announcements, item/currency rewards and server-wide
/// Vote Parties. Implement on any Component or GameObjectSystem in your active scene — no manual
/// += / -= needed and safe across hotloads.
/// </summary>
public interface ISboxlistListener : ISceneEvent<ISboxlistListener>
{
	void OnVoteReceived( VoteInfo vote ) { }
	void OnVoteReward( RewardInfo reward ) { }
	void OnVoteParty( VotePartyInfo party ) { }
}

/// <summary>
/// Scene event interface for client-side custom notifications. Implement on any Razor Panel
/// (@implements ISboxlistNotificationListener), PanelComponent or Component in your scene, and set
/// notification.Handled = true to replace the default chat output with your own UI.
/// </summary>
public interface ISboxlistNotificationListener : ISceneEvent<ISboxlistNotificationListener>
{
	void OnVoteNotification( VoteNotification notification ) { }
}

/// <summary>
/// Static hooks for games that prefer C# events over scene interfaces (ISboxlistListener /
/// ISboxlistNotificationListener). VoteReceived, RewardReceived and VotePartyReached fire on the
/// host; NotificationReceived and ToastShown fire on the target client.
/// </summary>
public static class SboxlistEvents
{
	public static event Action<VoteInfo> VoteReceived;
	public static event Action<RewardInfo> RewardReceived;
	public static event Action<VotePartyInfo> VotePartyReached;
	public static event Action<VoteNotification> NotificationReceived;
	public static event Action<string> ToastShown;

	internal static void RaiseVote( VoteInfo vote )
	{
		Safe( () => ISceneEvent<ISboxlistListener>.Post( x => x.OnVoteReceived( vote ) ), "ISboxlistListener.OnVoteReceived" );
		Safe( () => VoteReceived?.Invoke( vote ), "VoteReceived" );
	}

	internal static void RaiseVoteParty( VotePartyInfo party )
	{
		Safe( () => ISceneEvent<ISboxlistListener>.Post( x => x.OnVoteParty( party ) ), "ISboxlistListener.OnVoteParty" );
		Safe( () => VotePartyReached?.Invoke( party ), "VotePartyReached" );
	}

	internal static void DispatchReward( RewardInfo reward )
	{
		try
		{
			ISceneEvent<ISboxlistListener>.Post( x => x.OnVoteReward( reward ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[sboxlist] an ISboxlistListener.OnVoteReward handler threw: {e.Message}" );
			reward.Refuse( "handler threw: " + e.Message );
			return;
		}

		var handlers = RewardReceived;
		if ( handlers is not null )
		{
			reward.Handled = true;
			try
			{
				handlers.Invoke( reward );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[sboxlist] a RewardReceived handler threw: {e.Message}" );
				reward.Refuse( "handler threw: " + e.Message );
			}
		}
	}

	internal static void DispatchNotification( VoteNotification notification )
	{
		Safe( () => ISceneEvent<ISboxlistNotificationListener>.Post( x => x.OnVoteNotification( notification ) ), "ISboxlistNotificationListener" );
		Safe( () => NotificationReceived?.Invoke( notification ), "NotificationReceived" );
	}

	internal static void RaiseToast( string text ) => Safe( () => ToastShown?.Invoke( text ), "ToastShown" );

	static void Safe( Action action, string name )
	{
		try { action(); }
		catch ( Exception e ) { Log.Warning( $"[sboxlist] a {name} handler threw: {e.Message}" ); }
	}
}
