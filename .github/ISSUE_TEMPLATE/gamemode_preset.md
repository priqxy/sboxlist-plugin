---
name: "Gamemode Integration / Preset Request"
about: "Tell us what currency, inventory, or RP character system your s&box gamemode uses and we'll write the C# hook & preset"
title: "[Gamemode Preset] "
labels: ["enhancement"]
---

## Your s&box Gamemode or Server
- **Gamemode / Package ID:** `org.ident`
- **Server page on sboxlist.com (optional):** `https://sboxlist.com/server/...`

## Wallet / Inventory / RP Name API
Paste the method or component signatures your gamemode uses to:
1. **Add currency / points:** (e.g. `player.Components.Get<Wallet>()?.AddCash(amount)`)
2. **Grant an inventory item:** (e.g. `Inventory.TryGive(player, itemId, count)`)
3. **Get the player's RP character name (if applicable):** (e.g. `Character.GetActiveName(player)`)

We will put together a ready-to-drop `GamemodeVoteRewards.cs` for your project and add a 1-click preset on https://sboxlist.com/plugin.
