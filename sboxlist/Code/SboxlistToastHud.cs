using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Sboxlist;

/// <summary>
/// Optional ready-made on-screen toast HUD component. Add this component alongside SboxlistService
/// (it automatically creates a ScreenPanel if none is on the GameObject) to show dark vote-reward
/// toast banners in the top-right corner without writing custom UI code. Games with their own
/// Razor HUD can omit this component and implement ISboxlistNotificationListener instead.
/// </summary>
public sealed class SboxlistToastHud : PanelComponent, ISboxlistNotificationListener
{
	sealed class Entry
	{
		public string Text;
		public double ExpiresAt;
	}

	/// <summary>How many seconds each toast card stays on screen.</summary>
	[Property] public float DisplaySeconds { get; set; } = 6.0f;

	/// <summary>When true, suppresses the duplicate "★ ..." line in Sandbox.Platform.Chat for toasts.</summary>
	[Property] public bool SuppressDefaultChat { get; set; } = false;

	readonly List<Entry> entries = new();

	protected override void OnStart()
	{
		base.OnStart();
		if ( !Application.IsDedicatedServer )
			Components.GetOrCreate<ScreenPanel>();
	}

	public void OnVoteNotification( VoteNotification notification )
	{
		if ( notification is null || notification.Kind != "toast" ) return;
		if ( SuppressDefaultChat ) notification.Handled = true;
		if ( Application.IsDedicatedServer ) return;

		var duration = DisplaySeconds < 1f ? 1f : DisplaySeconds;
		entries.Add( new Entry { Text = "★ " + notification.Text, ExpiresAt = RealTime.NowDouble + duration } );
		while ( entries.Count > 5 ) entries.RemoveAt( 0 );
		Rebuild();
	}

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		if ( Panel is null ) return;
		Panel.Style.Position = PositionMode.Absolute;
		Panel.Style.Top = 24;
		Panel.Style.Right = 24;
		Panel.Style.FlexDirection = FlexDirection.Column;
		Panel.Style.ZIndex = 1000;
		Rebuild();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( entries.Count == 0 ) return;
		var now = RealTime.NowDouble;
		var removed = false;
		for ( var i = entries.Count - 1; i >= 0; i-- )
		{
			if ( entries[i].ExpiresAt <= now )
			{
				entries.RemoveAt( i );
				removed = true;
			}
		}
		if ( removed ) Rebuild();
	}

	void Rebuild()
	{
		if ( Panel is null ) return;
		Panel.DeleteChildren( true );
		foreach ( var entry in entries )
		{
			var card = Panel.AddChild<Label>();
			card.Text = entry.Text;
			card.Style.BackgroundColor = new Color( 0.09f, 0.11f, 0.13f, 0.95f );
			card.Style.FontColor = Color.White;
			card.Style.FontSize = 16;
			card.Style.FontWeight = 600;
			card.Style.PaddingLeft = 14;
			card.Style.PaddingRight = 14;
			card.Style.PaddingTop = 10;
			card.Style.PaddingBottom = 10;
			card.Style.MarginBottom = 8;
			card.Style.BorderLeftWidth = 4;
			card.Style.BorderLeftColor = new Color( 0.31f, 0.27f, 0.90f );
		}
	}
}
