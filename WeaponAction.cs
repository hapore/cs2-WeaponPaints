using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
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
		/// <summary>
		/// ¿Al jugador se le aplican sus items? Con `SkinsPermissionFlag` vacío, a todos
		/// (comportamiento original); si no, sólo a quien tenga ese flag.
		///
		/// El chequeo va en la APLICACIÓN y no en la carga a propósito. Los dos plugins
		/// enganchan `EventPlayerConnectFull` y el de permisos publica sus flags de forma
		/// asíncrona, así que al conectar todavía no están: preguntar ahí le negaba los
		/// items a todo el mundo, y esperarlos retrasaba la carga lo suficiente como para
		/// que el cuchillo —lo primero que se entrega, en el mismo instante del spawn—
		/// llegara antes que sus datos y saliera vanilla. Estas funciones corren en el
		/// spawn o después, cuando los permisos hace rato que están resueltos.
		///
		/// Los flags salen del AdminManager de CounterStrikeSharp, que es la API de
		/// permisos del framework: no se consulta la base ni se conoce al otro plugin.
		///
		/// Como se evalúa en cada aplicación, un VIP que vence deja de recibir sus items
		/// en el siguiente spawn, sin necesidad de reconectar ni de borrar nada.
		///
		/// Deliberadamente NO cachea. El AdminManager no distingue "todavía no resolví a este
		/// jugador" de "este jugador no tiene permisos": las dos cosas son ausencia en el mismo
		/// diccionario. Cualquier caché que intente cubrir la ventana inicial termina siendo el
		/// estado permanente para todo jugador común, y deja pegado un `true` viejo cuando a un
		/// VIP le limpian los permisos — justo el caso que tiene que cortar.
		///
		/// La ventana inicial se cubre reaplicando, no adivinando: ver
		/// <see cref="ResolveSkinsAccessThenApply"/>.
		/// </summary>
		internal static bool HasSkinsAccess(CCSPlayerController? player)
		{
			var flag = _config.SkinsPermissionFlag;

			if (string.IsNullOrWhiteSpace(flag)) return true;
			if (player == null || !player.IsValid || player.IsBot) return false;

			return AdminManager.PlayerHasPermissions(player, flag);
		}

		private void GivePlayerWeaponSkin(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			if (!Config.Additional.SkinEnabled) return;
			if (!HasSkinsAccess(player)) return;
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

			// Las dos listas de atributos se vaciaron más arriba (RemoveAll). Las variables
			// fallback alcanzan para los paint kits viejos, pero los nuevos los compone el
			// cliente a partir de los atributos econ: si no vuelven prefab, seed y wear en
			// AMBAS listas, el patrón se arma con la escala y rotación por defecto.
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weapon.FallbackPaintKit);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", weapon.FallbackSeed);

			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", weapon.FallbackPaintKit);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", weapon.FallbackSeed);

			ApplyWear(weapon, weaponInfo.Wear);

			// El mismo flag que ya pone el camino de guantes. Sin esto el cliente considera
			// que el item econ no está completo y compone el acabado por su camino por
			// defecto: el arma sale "vanilla" aunque el paint kit haya viajado.
			weapon.AttributeManager.Item.Initialized = true;

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
		// The client only rebuilds the sticker composite when the item actually changes, so a
		// refresh has to hand it a wear different from the one it already holds. Toggling between
		// the stored wear and stored + 0.001 is enough, and unlike an accumulator it always comes
		// back to the value the player picked: the old code fed its own previous result back in and
		// ignored the database, so lowering the float on the website could never restore factory new.
		private void IncrementWearForWeaponWithStickers(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null ||
			    weaponInfo.Stickers.Count <= 0) return;

			const float wearNudge = 0.001f;
			float baseWear = weaponInfo.Wear;
			// El empujón va hacia arriba salvo en el tope: saturarlo en 1.0 dejaría los
			// dos lados del toggle en el mismo número, el cliente no vería cambio y el
			// composite no se rehace. Con wear 1.0 se empuja para abajo.
			float nudgedWear = baseWear + wearNudge <= 1.0f ? baseWear + wearNudge : baseWear - wearNudge;

			var playerWear = _temporaryPlayerWeaponWear.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<int, float>());

			float appliedWear = playerWear.AddOrUpdate(
				weaponDefIndex,
				nudgedWear,
				(_, previous) => previous.Equals(baseWear) ? nudgedWear : baseWear
			);

			// Por los tres lugares, no sólo el fallback: si el atributo econ se quedara con
			// el wear anterior, el cliente compondría contra un valor que el servidor ya no
			// quiso mandar y el toggle dejaría de verse como un cambio de item.
			ApplyWear(weapon, appliedWear);
		}

		/// <summary>
		/// El wear vive en tres lugares que tienen que coincidir: la variable fallback y el
		/// atributo "set item texture wear" de las DOS listas. Escribir uno solo deja al
		/// cliente componiendo contra un wear que el servidor nunca quiso mandar.
		/// </summary>
		private static void ApplyWear(CBasePlayerWeapon weapon, float wear)
		{
			weapon.FallbackWear = wear;
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", wear);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", wear);
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
			// La falta de permiso entra por acá y no por un return arriba, a propósito: así
			// toma el mismo camino de reset que "no tiene guante configurado". A quien se le
			// venció el VIP con guantes ya aplicados se le devuelven los suyos en el
			// siguiente spawn, en vez de quedarse con los del plugin hasta reconectar.
			if (!HasSkinsAccess(player) ||
			    !GPlayersGlove.TryGetValue(player.Slot, out var gloveInfo) ||
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
			if (!HasSkinsAccess(player)) return;
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

		/// <summary>
		/// Resuelve qué kit corresponde al bando en el que está el jugador. Dos estados que hay
		/// que mantener separados y que un `TryGetValue(...) || id == 0` confunde:
		///
		/// - La clave NO está: nunca eligió nada para ese bando. El menú escribe sólo el bando
		///   actual salvo que esté sin equipo (Commands.cs), así que esto es lo normal, no un
		///   caso viejo. Ahí sí vale usar el kit del otro bando: es el que el jugador eligió.
		/// - La clave está en 0: eligió "None" explícitamente para ese bando. Se respeta y no se
		///   toca nada, que es lo que deja sonar el kit de su propio inventario.
		///
		/// Lo usan los dos canales de audio (m_iMusicKitID y el evento round_mvp) para que no
		/// puedan discrepar.
		/// </summary>
		internal static bool TryResolveMusicKit(ConcurrentDictionary<CsTeam, ushort> musicInfo, CsTeam team, out ushort musicId)
		{
			if (musicInfo.TryGetValue(team, out musicId))
				return musicId != 0;

			musicId = musicInfo.Values.FirstOrDefault(id => id != 0);
			return musicId != 0;
		}

		private static void GivePlayerMusicKit(CCSPlayerController player)
		{
			if (player.IsBot) return;
			if (!HasSkinsAccess(player)) return;
			if (!GPlayersMusic.TryGetValue(player.Slot, out var musicInfo)) return;
			if (!TryResolveMusicKit(musicInfo, player.Team, out var musicId)) return;

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
			if (!HasSkinsAccess(player)) return;
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
