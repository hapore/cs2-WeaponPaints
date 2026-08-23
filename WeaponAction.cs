using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
	public partial class WeaponPaints
	{
		private void GivePlayerWeaponSkin(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			if (!Config.Additional.SkinEnabled) return;
			if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out _)) return;
			
			bool isKnife = weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
			
			switch (isKnife)
			{
				case true when !HasChangedKnife(player, out var _):
					return;
				
				case true:
				{
					var newDefIndex = WeaponDefindex.FirstOrDefault(x => x.Value == GPlayersKnife[player.Slot][player.Team]);
					if (newDefIndex.Key == 0) return;

					if (weapon.AttributeManager.Item.ItemDefinitionIndex != newDefIndex.Key)
					{
						SubclassChange(weapon, (ushort)newDefIndex.Key);
					}

					weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)newDefIndex.Key;
					weapon.AttributeManager.Item.EntityQuality = 3;
					
					weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
					weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
					break;
				}
				default:
					weapon.AttributeManager.Item.EntityQuality = 0;
					break;
			}

			UpdatePlayerEconItemId(weapon.AttributeManager.Item);

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
			int fallbackPaintKit;
			
			weapon.AttributeManager.Item.AccountID = (uint)player.SteamID;
			
			List<JObject> skinInfo;
			bool isLegacyModel;

			if (_config.Additional.GiveRandomSkin &&
			    !HasChangedPaint(player, weaponDefIndex, out _))
			{
				// Random skins
				weapon.FallbackPaintKit = GetRandomPaint(weaponDefIndex);
				weapon.FallbackSeed = 0;
				weapon.FallbackWear = 0.01f;
			
				weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", 0);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", 0.01f);
			
				weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", 0);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", 0.01f);
			
				fallbackPaintKit = weapon.FallbackPaintKit;
			
				if (fallbackPaintKit == 0)
					return;
			
				skinInfo = SkinsList
					.Where(w => 
						w["weapon_defindex"]?.ToObject<int>() == weaponDefIndex && 
						w["paint"]?.ToObject<int>() == fallbackPaintKit)
					.ToList();
				
				isLegacyModel = skinInfo.Count <= 0 || skinInfo[0].Value<bool>("legacy_model");
				UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
				return;
			}

			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return;

			//Log($"Apply on {weapon.DesignerName}({weapon.AttributeManager.Item.ItemDefinitionIndex}) paint {gPlayerWeaponPaints[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} seed {gPlayerWeaponSeed[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} wear {gPlayerWeaponWear[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]}");

			weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
			weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			
			UpdatePlayerEconItemId(weapon.AttributeManager.Item);

			weapon.AttributeManager.Item.CustomName = weaponInfo.Nametag;
			weapon.FallbackPaintKit = weaponInfo.Paint;
			
			weapon.FallbackSeed = weaponInfo is { Paint: 38, Seed: 0 } ? _fadeSeed++ : weaponInfo.Seed;
			
			weapon.FallbackWear = weaponInfo.Wear;
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weapon.FallbackPaintKit);

			if (weaponInfo.StatTrak)
			{			
				weapon.AttributeManager.Item.EntityQuality = 9;

				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);
				
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater score type", 0);
			}

			fallbackPaintKit = weapon.FallbackPaintKit;

			if (fallbackPaintKit == 0)
				return;

			if (weaponInfo.KeyChain != null) SetKeychain(player, weapon);
			if (weaponInfo.Stickers.Count > 0) SetStickers(player, weapon);

			skinInfo = SkinsList
				.Where(w => 
					w["weapon_defindex"]?.ToObject<int>() == weaponDefIndex && 
					w["paint"]?.ToObject<int>() == fallbackPaintKit)
				.ToList();
				
			isLegacyModel = skinInfo.Count <= 0 || skinInfo[0].Value<bool>("legacy_model");
			UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
		}
		
		// silly method to update sticker when call RefreshWeapons()
		private void IncrementWearForWeaponWithStickers(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null ||
			    weaponInfo.Stickers.Count <= 0) return;
			
			float wearIncrement = 0.001f;
			float currentWear = weaponInfo.Wear;

			var playerWear = _temporaryPlayerWeaponWear.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<int, float>());

			float incrementedWear = playerWear.AddOrUpdate(
				weaponDefIndex,
				currentWear + wearIncrement,
				(_, oldWear) => Math.Min(oldWear + wearIncrement, 1.0f)
			);

			weapon.FallbackWear = incrementedWear;
		}

		private void SetStickers(CCSPlayerController? player, CBasePlayerWeapon weapon)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player ,weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return;

			foreach (var sticker in weaponInfo.Stickers)
			{
				int stickerSlot = weaponInfo.Stickers.IndexOf(sticker);

				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} id", ViewAsFloat(sticker.Id));
				if (sticker.OffsetX != 0 || sticker.OffsetY != 0)
					CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
						$"sticker slot {stickerSlot} schema", 0);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset x", sticker.OffsetX);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset y", sticker.OffsetY);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} wear", sticker.Wear);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} scale", sticker.Scale);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} rotation", sticker.Rotation);
			}

			if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var playerWear) &&
				playerWear.TryGetValue(weaponDefIndex, out float storedWear))
			{
				weapon.FallbackWear = storedWear;
			}
		}

		private void SetKeychain(CCSPlayerController? player, CBasePlayerWeapon weapon)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player, weaponDefIndex, out var value) || value?.KeyChain == null)
				return;
			
			var keyChain = value.KeyChain;

			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 id", ViewAsFloat(keyChain.Id));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset x", keyChain.OffsetX);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset y", keyChain.OffsetY);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset z", keyChain.OffsetZ);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 seed", ViewAsFloat(keyChain.Seed));
		}

		private static void GiveKnifeToPlayer(CCSPlayerController? player)
		{
			if (!_config.Additional.KnifeEnabled || player == null || !player.IsValid) return;

			if (PlayerHasKnife(player)) return;

			//string knifeToGive = (CsTeam)player.TeamNum == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";
			player.GiveNamedItem(CsItem.Knife);
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
		}

		private static bool PlayerHasKnife(CCSPlayerController? player)
		{
			if (!_config.Additional.KnifeEnabled) return false;

			if (player == null || !player.IsValid || !player.PlayerPawn.IsValid)
			{
				return false;
			}

			if (player.PlayerPawn.Value == null || player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
				return false;

			var weapons = player.PlayerPawn.Value.WeaponServices?.MyWeapons;
			if (weapons == null) return false;
			foreach (var weapon in weapons)
			{
				if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid) continue;
				if (weapon.Value.DesignerName.Contains("knife") || weapon.Value.DesignerName.Contains("bayonet"))
				{
					return true;
				}
			}
			return false;
		}

		private void RefreshWeapons(CCSPlayerController? player)
		{
			if (!_gBCommandsAllowed) return;
			if (player == null || !player.IsValid || player.PlayerPawn.Value == null || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
				return;
			if (player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
				return;

			var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;

			if (weapons.Count == 0)
				return;
			if (player.Team is CsTeam.None or CsTeam.Spectator)
				return;

			var hasKnife = false;
			
			Dictionary<string, List<(int, int)>> weaponsWithAmmo = [];

			foreach (var weapon in weapons)
			{
				if (!weapon.IsValid || weapon.Value == null ||
					!weapon.Value.IsValid || !weapon.Value.DesignerName.Contains("weapon_"))
					continue;
				
				CCSWeaponBaseGun gun = weapon.Value.As<CCSWeaponBaseGun>();

				if (weapon.Value.Entity == null) continue;
				if (!weapon.Value.OwnerEntity.IsValid) continue;
				if (gun.Entity == null) continue;
				if (!gun.IsValid) continue;

				try
				{
					CCSWeaponBaseVData? weaponData = weapon.Value.As<CCSWeaponBase>().VData;

					if (weaponData == null) continue;

					if (weaponData.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)
					{
						if (!WeaponDefindex.TryGetValue(weapon.Value.AttributeManager.Item.ItemDefinitionIndex, out var weaponByDefindex))
							continue;

						int clip1 = weapon.Value.Clip1;
						int reservedAmmo = weapon.Value.ReserveAmmo[0];

						if (!weaponsWithAmmo.TryGetValue(weaponByDefindex, out var value))
						{
							value = [];
							weaponsWithAmmo.Add(weaponByDefindex, value);
						}

						value.Add((clip1, reservedAmmo));

						if (gun.VData == null) return;
						
						weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
					}

					if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_KNIFE)
					{
						weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
						hasKnife = true;
					}
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex.Message);
				}
			}

			AddTimer(0.23f, () =>
					{
						if (!_gBCommandsAllowed) return;

						if (!PlayerHasKnife(player) && hasKnife)
						{
							var newKnife = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.Knife));
							var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.USP));
							player.GiveNamedItem(CsItem.Knife);
							player.ExecuteClientCommand("slot3");

							Server.NextFrame(() =>
							{
								try
								{
									if (newKnife != null && newKnife.IsValid)
										newKnife.AddEntityIOEvent("Kill", newKnife, null, "", 0.01f);
									if (newWeapon != null && newWeapon.IsValid)
										newWeapon.AddEntityIOEvent("Kill", newWeapon, null, "", 0.01f);
								}
								catch (Exception ex)
								{
									Logger.LogWarning("Error AddEntityIOEvent " + ex.Message);
								}
							});
						}


						foreach (var entry in weaponsWithAmmo)
						{
							foreach (var ammo in entry.Value)
							{
								var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(entry.Key));
								Server.NextFrame(() =>
						{
							try
							{
								newWeapon.Clip1 = ammo.Item1;
								newWeapon.ReserveAmmo[0] = ammo.Item2;

								IncrementWearForWeaponWithStickers(player, newWeapon);
							}
							catch (Exception ex)
							{
								Logger.LogWarning("Error setting weapon properties: " + ex.Message);
							}
						});
							}
						}
					}, TimerFlags.STOP_ON_MAPCHANGE);
		}

		private void GivePlayerGloves(CCSPlayerController player)
		{
			if (!Utility.IsPlayerValid(player) || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE) return;

			CCSPlayerPawn? pawn = player.PlayerPawn.Value;
			if (pawn == null || !pawn.IsValid)
				return;

			// Resolve the glove BEFORE touching the econ item. Wiping the attributes of a player
			// who has no plugin glove would destroy the networked attributes of the gloves he owns
			// in his own inventory: he would keep seeing them (predicted client side) while everyone
			// else would render empty/default hands.
			if (!GPlayersGlove.TryGetValue(player.Slot, out var gloveInfo) ||
			    !gloveInfo.TryGetValue(player.Team, out var gloveId) ||
			    gloveId == 0 ||
			    !HasChangedPaint(player, gloveId, out var weaponInfo) || weaponInfo == null)
			{
				// Only undo what we applied ourselves ("Gloves | Default", team without a glove, ...).
				// If we never touched this pawn there is nothing to reset and clearing the item here
				// would destroy the gloves the player owns in his own inventory.
				if (GPlayersGloveApplied.TryRemove(player.Slot, out _))
					ResetPlayerGloves(player, pawn);

				return;
			}

			GPlayersGloveApplied[player.Slot] = 1;

			CEconItemView item = pawn.EconGloves;

			item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			item.AttributeList.Attributes.RemoveAll();

			//force gloves model refresh to prevent model overlap
			player.ExecuteClientCommand("lastinv");
			Instance.AddTimer(0.08f, () =>
			{	
				try
				{
					if (!player.IsValid)
						return;

					if (!player.PawnIsAlive)
						return;

					CCSPlayerPawn? currentPawn = player.PlayerPawn.Value;
					if (currentPawn == null || !currentPawn.IsValid)
						return;

					CEconItemView currentItem = currentPawn.EconGloves;

					currentItem.ItemDefinitionIndex = gloveId;
					
					UpdatePlayerEconItemId(currentItem);

					currentItem.NetworkedDynamicAttributes.Attributes.RemoveAll();
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.NetworkedDynamicAttributes.Handle, "set item texture prefab", weaponInfo.Paint);
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.NetworkedDynamicAttributes.Handle, "set item texture seed", weaponInfo.Seed);
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.NetworkedDynamicAttributes.Handle, "set item texture wear", weaponInfo.Wear);

					currentItem.AttributeList.Attributes.RemoveAll();
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.AttributeList.Handle, "set item texture prefab", weaponInfo.Paint);
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.AttributeList.Handle, "set item texture seed", weaponInfo.Seed);
					CAttributeListSetOrAddAttributeValueByName.Invoke(currentItem.AttributeList.Handle, "set item texture wear", weaponInfo.Wear);

					currentItem.Initialized = true;

					// m_EconGloves is only sent to other clients when it is flagged as dirty.
					// Without this the owner sees his glove (local prediction) but everybody else
					// keeps the state the pawn was spawned with.
					Utilities.SetStateChanged(currentPawn, "CCSPlayerPawn", "m_EconGloves");

					//force gloves model refresh to prevent model overlap
					player.ExecuteClientCommand("lastinv");

					// hides the hands baked into the player model so the econ glove is the only
					// thing rendered on the world model (what the other players look at)
					SetBodygroup(currentPawn, "default_gloves", 1);

					SetBodygroup(currentPawn, "first_or_third_person", 0);
					AddTimer(0.2f, () =>
					{
						if (!player.IsValid || !player.PawnIsAlive)
							return;

						CCSPlayerPawn? refreshPawn = player.PlayerPawn.Value;
						if (refreshPawn == null || !refreshPawn.IsValid)
							return;

						SetBodygroup(refreshPawn, "first_or_third_person", 1);
					}, TimerFlags.STOP_ON_MAPCHANGE);
				}
				catch (Exception) { }
			}, TimerFlags.STOP_ON_MAPCHANGE);
		}

		// Removes a glove the plugin applied earlier and puts the pawn back on the hands
		// baked into its model. Never call this for a player we did not modify.
		private void ResetPlayerGloves(CCSPlayerController player, CCSPlayerPawn pawn)
		{
			try
			{
				CEconItemView item = pawn.EconGloves;

				item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				item.AttributeList.Attributes.RemoveAll();

				item.ItemDefinitionIndex = 0;
				item.ItemID = 0;
				item.ItemIDLow = 0;
				item.ItemIDHigh = 0;
				item.Initialized = false;

				Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");

				SetBodygroup(pawn, "default_gloves", 0);

				player.ExecuteClientCommand("lastinv");
				SetBodygroup(pawn, "first_or_third_person", 0);
				AddTimer(0.2f, () =>
				{
					if (!player.IsValid || !player.PawnIsAlive)
						return;

					CCSPlayerPawn? refreshPawn = player.PlayerPawn.Value;
					if (refreshPawn == null || !refreshPawn.IsValid)
						return;

					SetBodygroup(refreshPawn, "first_or_third_person", 1);
				}, TimerFlags.STOP_ON_MAPCHANGE);
			}
			catch (Exception) { }
		}

		private static int GetRandomPaint(int defindex)
		{
			if (SkinsList.Count == 0)
				return 0;

			Random rnd = new Random();

			// Filter weapons by the provided defindex
			var filteredWeapons = SkinsList.Where(w => w["weapon_defindex"]?.ToString() == defindex.ToString()).ToList();

			if (filteredWeapons.Count == 0)
				return 0;

			var randomWeapon = filteredWeapons[rnd.Next(filteredWeapons.Count)];

			return int.TryParse(randomWeapon["paint"]?.ToString(), out var paintValue) ? paintValue : 0;
		}

		//xstage idea on css discord
		public static void SubclassChange(CBasePlayerWeapon weapon, ushort itemD)
		{
			weapon.AcceptInput("ChangeSubclass", value: itemD.ToString());
		}

		public static void SetBodygroup(CCSPlayerPawn pawn, string group, int value)
		{
			pawn.AcceptInput("SetBodygroup", value:$"{group},{value}");
		}

		private void UpdateWeaponMeshGroupMask(CBaseEntity weapon, bool isLegacy = false)
		{
				if (weapon.CBodyComponent?.SceneNode == null) return;
				//var skeleton = weapon.CBodyComponent.SceneNode.GetSkeletonInstance();
				// skeleton.ModelState.MeshGroupMask = isLegacy ? 2UL : 1UL;

				weapon.AcceptInput("SetBodygroup", value: $"body,{(isLegacy ? 1 : 0)}");
		}

		private void UpdatePlayerWeaponMeshGroupMask(CCSPlayerController player, CBasePlayerWeapon weapon, bool isLegacy)
		{
			UpdateWeaponMeshGroupMask(weapon, isLegacy);
		}

		private static void GivePlayerAgent(CCSPlayerController player)
		{
			if (!GPlayersAgent.TryGetValue(player.Slot, out var value)) return;

			var model = player.TeamNum == 3 ? value.CT : value.T;
			if (string.IsNullOrEmpty(model)) return;

			if (player.PlayerPawn.Value == null)
				return;

			try
			{
				Server.NextFrame(() =>
				{
					if (!player.IsValid)
						return;

					CCSPlayerPawn? pawn = player.PlayerPawn.Value;
					if (pawn == null || !pawn.IsValid)
						return;

					pawn.SetModel(
						$"agents/models/{model}.vmdl"
					);

					ApplyAgentCharacter(player, pawn, model);
				});
			}
			catch (Exception)
			{
			}
		}

		// SetModel only swaps the mesh: the game keeps thinking the player wears the default
		// character of his team, so it falls back to the generic voice bank. m_strVOPrefix is
		// the field that actually names the voice bank ("vo_prefix" in items_game.txt).
		private static void ApplyAgentCharacter(CCSPlayerController player, CCSPlayerPawn pawn, string model)
		{
			if (!AgentDefIndexes.TryGetValue(model, out var defIndex) || defIndex == 0)
				return;

			try
			{
				pawn.CharacterDefIndex = defIndex;
				Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nCharacterDefIndex");

				player.PawnCharacterDefIndex = defIndex;
				Utilities.SetStateChanged(player, "CCSPlayerController", "m_nPawnCharacterDefIndex");
			}
			catch (Exception)
			{
			}

			// Agents without an entry here have no voice of their own and must keep the
			// standard team lines.
			if (!AgentVoicePrefixes.TryGetValue(defIndex, out var voPrefix) || string.IsNullOrEmpty(voPrefix))
				return;

			try
			{
				var previous = Schema.GetString(pawn.Handle, "CCSPlayerPawn", "m_strVOPrefix");

				Schema.SetString(pawn.Handle, "CCSPlayerPawn", "m_strVOPrefix", voPrefix);
				Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_strVOPrefix");

				// items_game.txt encodes the gender in the prefix itself, so this needs no guessing
				pawn.HasFemaleVoice = voPrefix.Contains("fem", StringComparison.OrdinalIgnoreCase);
				Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_bHasFemaleVoice");

				Utility.Log($"[agent voice] {player.PlayerName}: agent {defIndex} ({model}) vo_prefix \"{previous}\" -> \"{voPrefix}\" (female: {pawn.HasFemaleVoice})");
			}
			catch (Exception ex)
			{
				Utility.Log($"[agent voice] failed to set vo_prefix for agent {defIndex}: {ex.Message}");
			}
		}

		private static void GivePlayerMusicKit(CCSPlayerController player)
		{
			if (player.IsBot) return;
			if (!GPlayersMusic.TryGetValue(player.Slot, out var musicInfo) ||
			    !musicInfo.TryGetValue(player.Team, out var musicId) || musicId == 0) return;
			
			if (player.InventoryServices == null) return;

			player.MusicKitID = musicId;
			// player.MvpNoMusic = false;
			player.InventoryServices.MusicID = musicId;
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
			// Utilities.SetStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
			// player.MusicKitMVPs = musicId;
			// Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
		}

		private static void GivePlayerPin(CCSPlayerController player)
		{
			if (!GPlayersPin.TryGetValue(player.Slot, out var pinInfo) ||
			    !pinInfo.TryGetValue(player.Team, out var pinId)) return;
			if (player.InventoryServices == null) return;
			
			player.InventoryServices.Rank[5] = pinId > 0 ? (MedalRank_t)pinId : MedalRank_t.MEDAL_RANK_NONE;
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
		}
		
		private void GiveOnItemPickup(CCSPlayerController player)
		{
			var pawn = player.PlayerPawn.Value;
			if (pawn == null) return;
			
			var myWeapons = pawn.WeaponServices?.MyWeapons;
			if (myWeapons == null) return;
			
			foreach (var handle in myWeapons)
			{
				var weapon = handle.Value;
			
				if (weapon == null || !weapon.IsValid) continue;
				if (myWeapons.Count == 1)
				{
					var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.USP));
					weapon.AddEntityIOEvent("Kill", weapon, null, "", 0.01f);
					player.GiveNamedItem(CsItem.Knife);
					player.ExecuteClientCommand("slot3");
					newWeapon.AddEntityIOEvent("Kill", newWeapon, null, "", 0.01f);
				}
					
				GivePlayerWeaponSkin(player, weapon);
			}
		}
		
		private void UpdatePlayerEconItemId(CEconItemView econItemView)
		{
			var itemId = _nextItemId++;
			
			econItemView.ItemID = itemId;
			econItemView.ItemIDLow = (uint)itemId & 0xFFFFFFFF;
			econItemView.ItemIDHigh = (uint)itemId >> 32;
		}

		private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
		{
			var pawn = itemServices.Pawn.Value;
			if (!pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value == null) return null;
			var player = new CCSPlayerController(pawn.Controller.Value.Handle);
			return !Utility.IsPlayerValid(player) ? null : player;
		}

		private static bool HasChangedKnife(CCSPlayerController player, out string? knifeValue)
		{
			knifeValue = null;

			// Check if player has knife info for their slot and team
			if (!GPlayersKnife.TryGetValue(player.Slot, out var knife) ||
			    !knife.TryGetValue(player.Team, out var value) ||
			    value == "weapon_knife") return false;
			knifeValue = value; // Assign the knife value to the out parameter
			return true;
		}
		
		private static bool HasChangedPaint(CCSPlayerController player, int weaponDefIndex, out WeaponInfo? weaponInfo)
		{
			weaponInfo = null;

			// Check if player has weapons info for their slot and team
			if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamInfo) || 
			    !teamInfo.TryGetValue(player.Team, out var teamWeapons))
			{
				return false;
			}

			// Check if the specified weapon has a paint/skin change
			if (!teamWeapons.TryGetValue(weaponDefIndex, out var value) || value.Paint <= 0) return false;
			
			weaponInfo = value; // Assign the out variable when it exists
			return true;
		}

		private static float ViewAsFloat(uint value)
		{
			return BitConverter.Int32BitsToSingle((int)value);
		}
	}
}
