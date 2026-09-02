using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace WeaponPaints
{
	public partial class WeaponPaints
	{
		// Levantado una vez que el MVP de la ronda ya fue reescrito, para que el evento sintético
		// que dispara el handler no vuelva a entrar por el mismo camino. Se limpia en AMBOS bordes
		// de ronda: limpiarlo sólo en RoundStart dejaba el flag pegado cuando una ronda no
		// disparaba uno (warmup, mp_restartgame, el manejo de rondas de los plugins de retakes),
		// y a partir de ahí todos los MVP siguientes sonaban con el kit por defecto.
		private bool _mvpMusicApplied;
		
		[GameEventHandler]
		public HookResult OnClientFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
     	{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || player.IsBot ||
				WeaponSync == null || Database == null) return HookResult.Continue;

			var playerInfo = new PlayerInfo
			{
				UserId = player.UserId,
				Slot = player.Slot,
				Index = (int)player.Index,
				SteamId = player.SteamID.ToString(),
				Name = player.PlayerName,
				IpAddress = player.IpAddress?.Split(":")[0]
			};

			try
			{
				var slot = player.Slot;
				var steamId = playerInfo.SteamId;

				_ = Task.Run(async () =>
				{
					await WeaponSync.GetPlayerData(playerInfo);

					// The query can finish after the player already spawned (slow database, or
					// somebody joining straight into a live round). Nothing re-applies on its
					// own, so without this he would keep his inventory items until his next
					// spawn, which is the "sometimes my skins don't load" case.
					Server.NextFrame(() =>
					{
						var connectedPlayer = Utilities.GetPlayerFromSlot(slot);

						// the slot may already belong to somebody else if he left in between
						if (connectedPlayer == null || !connectedPlayer.IsValid || connectedPlayer.IsBot ||
						    connectedPlayer.SteamID.ToString() != steamId)
							return;

						// The music kit and the pin live on the controller, so they apply with or
						// without a pawn. Gating them behind PawnIsAlive left anyone whose data
						// landed while he was still dead or spectating on the default music until
						// his next spawn.
						GivePlayerMusicKit(connectedPlayer);
						AddTimer(0.15f, () => GivePlayerPin(connectedPlayer));

						if (!connectedPlayer.PawnIsAlive)
							return;

						GivePlayerGloves(connectedPlayer);
						GivePlayerAgent(connectedPlayer);
						RefreshWeapons(connectedPlayer);
					});
				});

				ResolveSkinsAccessThenApply(player);
			}
			catch
			{
			}

			Players.Add(player);

			return HookResult.Continue;
		}

		[GameEventHandler]
		public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || player.IsBot) return HookResult.Continue;

			var playerInfo = new PlayerInfo
			{
				UserId = player.UserId,
				Slot = player.Slot,
				Index = (int)player.Index,
				SteamId = player.SteamID.ToString(),
				Name = player.PlayerName,
				IpAddress = player.IpAddress?.Split(":")[0]
			};

			Task.Run(async () => 
			{
				if (WeaponSync != null)
					await WeaponSync.SyncStatTrakToDatabase(playerInfo);

				if (Config.Additional.SkinEnabled)
				{
					GPlayerWeaponsInfo.TryRemove(player.Slot, out _);
				}
			});

			if (Config.Additional.KnifeEnabled)
			{
				GPlayersKnife.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.GloveEnabled)
			{
				GPlayersGlove.TryRemove(player.Slot, out _);
				GPlayersGloveApplied.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.AgentEnabled)
			{
				GPlayersAgent.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.MusicEnabled)
			{
				GPlayersMusic.TryRemove(player.Slot, out _);
			}
			if (Config.Additional.PinsEnabled)
			{
				GPlayersPin.TryRemove(player.Slot, out _);
			}
			
			_temporaryPlayerWeaponWear.TryRemove(player.Slot, out _);
			CommandsCooldown.Remove(player.Slot);
			Players.Remove(player);

			return HookResult.Continue;
		}

		private void OnMapStart(string mapName)
		{
			if (Config.Additional is { KnifeEnabled: false, SkinEnabled: false, GloveEnabled: false }) return;
			
			if (Database != null)
				WeaponSync = new WeaponSynchronization(Database, Config);

			_fadeSeed = 0;
			_nextItemId = MinimumCustomItemId;
		}

		private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Userid;

			if (player is null || !player.IsValid || Config.Additional is { KnifeEnabled: false, GloveEnabled: false })
				return HookResult.Continue;

			CCSPlayerPawn? pawn = player.PlayerPawn.Value;

			if (pawn == null || !pawn.IsValid)
				return HookResult.Continue;

			GivePlayerAgent(player);
			Server.NextFrame(() =>
			{
				GivePlayerGloves(player);

				// El juego escribe el kit real del inventario durante el spawn. Aplicarlo en el
				// mismo frame lo dejaba pisado, y con m_iMusicKitID en el kit del inventario el
				// deathcam y los demás stingers suenan con ese aunque el MVP salga bien.
				if (player.IsValid)
					GivePlayerMusicKit(player);
			});
			GivePlayerPin(player);

			return HookResult.Continue;
		}

		/// <summary>
		/// El plugin de permisos publica sus flags de forma asíncrona mientras el jugador
		/// conecta, así que su primer spawn puede ocurrir antes de que existan. Negar ahí no
		/// es una decisión sino falta de información, y el resultado es que entra vanilla:
		/// para el kit de música, además, es indistinguible de no tener uno asignado y suena
		/// el de su propio inventario.
		///
		/// Esto no adivina: sondea y sólo se detiene cuando efectivamente aplicó, o al agotarse.
		/// Importa que no sea un disparo único, porque reaplicar puede fallar en silencio de tres
		/// formas — los datos de la base todavía no llegaron, el jugador no tiene pawn vivo, o
		/// RefreshWeapons se cae por `_gBCommandsAllowed` entre RoundEnd y RoundStart. Cortar el
		/// temporizador antes de comprobarlas dejaba al jugador vanilla hasta el spawn siguiente,
		/// y en retakes las tres se dan todo el tiempo.
		/// </summary>
		private void ResolveSkinsAccessThenApply(CCSPlayerController player)
		{
			// Sin flag configurado no hay nada que esperar: HasSkinsAccess ya devuelve true.
			if (string.IsNullOrWhiteSpace(_config.SkinsPermissionFlag)) return;

			// Si el permiso ya está concedido, los caminos normales alcanzan. Arrancar el
			// sondeo igual haría que este y la continuación de GetPlayerData reapliquen lo
			// mismo, y dos RefreshWeapons seguidos matan y reentregan las armas dos veces.
			if (HasSkinsAccess(player)) return;

			var slot = player.Slot;
			var steamId = player.SteamID.ToString();
			var attempts = 0;
			var controllerItemsDone = false;
			CounterStrikeSharp.API.Modules.Timers.Timer? timer = null;

			// 40 x 0.25s = 10s. Sólo se agota con quien nunca recibe el flag o nunca llega a
			// spawnear; en cuanto se puede aplicar, se aplica y se corta.
			const int maxAttempts = 40;

			timer = AddTimer(0.25f, () =>
			{
				attempts++;

				var connectedPlayer = Utilities.GetPlayerFromSlot(slot);

				// the slot may already belong to somebody else if he left in between
				if (connectedPlayer == null || !connectedPlayer.IsValid || connectedPlayer.IsBot ||
				    connectedPlayer.SteamID.ToString() != steamId)
				{
					timer?.Kill();
					return;
				}

				var timedOut = attempts >= maxAttempts;

				// Sin permiso o sin datos todavía no hay nada que aplicar. Reaplicar acá
				// reentregaría las armas vanilla, que es justo el bug que esto viene a evitar.
				if (!HasSkinsAccess(connectedPlayer) || !GPlayerWeaponsInfo.ContainsKey(slot))
				{
					if (timedOut) timer?.Kill();
					return;
				}

				// El kit de música y el pin viven en el controller: no dependen del pawn ni de la
				// ventana de ronda, así que se aplican en cuanto se puede, y una sola vez.
				if (!controllerItemsDone)
				{
					controllerItemsDone = true;
					GivePlayerMusicKit(connectedPlayer);
					AddTimer(0.15f, () => GivePlayerPin(connectedPlayer));
				}

				// Lo demás necesita pawn vivo, y RefreshWeapons además necesita estar fuera de la
				// ventana de fin de ronda. Si ahora no se puede, se reintenta en el próximo tick.
				if (!connectedPlayer.PawnIsAlive || !_gBCommandsAllowed)
				{
					if (timedOut) timer?.Kill();
					return;
				}

				timer?.Kill();

				GivePlayerGloves(connectedPlayer);
				GivePlayerAgent(connectedPlayer);
				RefreshWeapons(connectedPlayer);
			}, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT |
			   CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
		}

		private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
		{
			_gBCommandsAllowed = false;

			// El MVP se otorga después de este evento, así que acá el flag se limpia justo antes
			// de que haga falta. El de RoundStart es la red de seguridad para las rondas que
			// terminan sin otorgar MVP.
			_mvpMusicApplied = false;
			return HookResult.Continue;
		}

		private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
		{
			_gBCommandsAllowed = true;
			_mvpMusicApplied = false;

			// El juego repone m_iMusicKitID desde el inventario real de Steam unos segundos
			// después de conectar, así que lo que escribimos en el spawn no sobrevive: en el TAB
			// se ve el kit correcto y al rato vuelve al del inventario. No hay forma de enganchar
			// esa carga, así que se reafirma en cada arranque de ronda.
			foreach (var player in Utilities.GetPlayers())
			{
				if (player is not { IsValid: true, IsBot: false }) continue;

				GivePlayerMusicKit(player);
			}

			return HookResult.Continue;
		}
		
		private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
		{
			if (_mvpMusicApplied)
				return HookResult.Continue;

			var player = @event.Userid;

			if (player == null || !player.IsValid || player.IsBot)
				return HookResult.Continue;

			// Este chequeo tiene que coincidir con el de GivePlayerMusicKit: mismo permiso y mismo
			// fallback de equipo, o el MVP suena distinto que el deathcam.
			if (!HasSkinsAccess(player))
				return HookResult.Continue;

			if (!GPlayersMusic.TryGetValue(player.Slot, out var musicInfo))
				return HookResult.Continue;

			if (!TryResolveMusicKit(musicInfo, player.Team, out var musicId))
				return HookResult.Continue;

			// El cliente elige el tema por m_iMusicKitID, no por el campo del evento. El log lo
			// mostró: el evento sintético salía con el kit correcto y aun así sonaba el del
			// inventario, porque el juego repone m_iMusicKitID después de nuestro spawn. Se
			// reescribe acá, que es el único instante que importa para el himno del MVP.
			GivePlayerMusicKit(player);

			@event.Musickitid = musicId;
			@event.Nomusic = 0;
			info.DontBroadcast = true;
			
			var newEvent = new EventRoundMvp(true)
			{
				Userid = player,
				Musickitid = musicId,
				Nomusic = 0,
			};

			// Se deja levantado, no se baja en un finally: si FireEvent resultara ser asíncrono,
			// bajarlo acá dejaría pasar el evento sintético y el handler se reescribiría a sí
			// mismo en bucle. Lo bajan los dos bordes de ronda de abajo.
			_mvpMusicApplied = true;

			newEvent.FireEvent(false);

			return HookResult.Continue;
		}

		private HookResult OnGiveNamedItemPost(DynamicHook hook)
		{
			try
			{
				var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
				var weapon = hook.GetReturn<CBasePlayerWeapon>();
				if (!weapon.DesignerName.Contains("weapon"))
					return HookResult.Continue;

				var player = GetPlayerFromItemServices(itemServices);
				if (player != null)
				{
					GivePlayerWeaponSkin(player, weapon);
				}
			}
			catch { }

			return HookResult.Continue;
		}

		private void OnEntityCreated(CEntityInstance entity)
		{
			var designerName = entity.DesignerName;

			if (designerName.Contains("weapon"))
			{
				Server.NextWorldUpdate(() =>
				{
					var weapon = new CBasePlayerWeapon(entity.Handle);
					if (!weapon.IsValid) return;

					try
					{
						SteamID? steamid = null;

						if (weapon.OriginalOwnerXuidLow > 0)
							steamid = new SteamID(weapon.OriginalOwnerXuidLow);

						CCSPlayerController? player;

						if (steamid != null && steamid.IsValid())
						{
							player = Players.FirstOrDefault(p => p.IsValid && p.SteamID == steamid.SteamId64);

							if (player == null)
								player = Utilities.GetPlayerFromSteamId(weapon.OriginalOwnerXuidLow);
						}
						else
						{
							CCSWeaponBaseGun gun = weapon.As<CCSWeaponBaseGun>();
							player = Utilities.GetPlayerFromIndex((int)weapon.OwnerEntity.Index) ?? Utilities.GetPlayerFromIndex((int)gun.OwnerEntity.Value!.Index);
						}

						if (string.IsNullOrEmpty(player?.PlayerName)) return;
						if (!Utility.IsPlayerValid(player)) return;
						
						GivePlayerWeaponSkin(player, weapon);
					}
					catch (Exception)
					{
					}
				});
			}
		}

		private void OnTick()
		{
			if (!Config.Additional.ShowSkinImage) return;

			foreach (var player in Players)
			{
				if (_playerWeaponImage.TryGetValue(player.Slot, out var value) && !string.IsNullOrEmpty(value))
				{
					player.PrintToCenterHtml("<img src='{PATH}'</img>".Replace("{PATH}", value));
				}
			}
		}
		
		[GameEventHandler]
		public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo _)
		{
			// if (!IsWindows) return HookResult.Continue;
			var player = @event.Userid;
			if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
			if (!@event.Item.Contains("knife")) return HookResult.Continue;
		
			var weaponDefIndex = (int)@event.Defindex;
				
			if (!HasChangedKnife(player, out var _) || !HasChangedPaint(player, weaponDefIndex, out var _))
				return HookResult.Continue;
			
			if (player is { Connected: PlayerConnectedState.Connected, PawnIsAlive: true, PlayerPawn.IsValid: true })
			{
				GiveOnItemPickup(player);
			}
		
			return HookResult.Continue;
		}

		private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
		{
			CCSPlayerController? player = @event.Attacker;
			CCSPlayerController? victim = @event.Userid;

			if (player is null || !player.IsValid)
				return HookResult.Continue;
			
			if (victim == null || !victim.IsValid || victim == player)
				return HookResult.Continue;
			
			CBasePlayerWeapon? weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

			if (weapon == null) return HookResult.Continue;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return HookResult.Continue;
				
			if (!weaponInfo.StatTrak) return HookResult.Continue;
			
			weaponInfo.StatTrakCount += 1;
				
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);
				
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater score type", 0);

			return HookResult.Continue;
		}

		private void RegisterListeners()
		{
			RegisterListener<Listeners.OnMapStart>(OnMapStart);

			RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
			RegisterEventHandler<EventRoundStart>(OnRoundStart);
			RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
			// Pre, no Post. RegisterEventHandler default es Post, y para entonces el evento ya
			// salió hacia los clientes: reescribir Musickitid y pedir DontBroadcast no cambia
			// nada, y suena el kit del inventario real del jugador. Se ve el correcto porque
			// m_iMusicKitID sí se escribe, pero el que suena viene del evento.
			RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Pre);
			RegisterListener<Listeners.OnEntitySpawned>(OnEntityCreated);
			RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

			if (Config.Additional.ShowSkinImage)
				RegisterListener<Listeners.OnTick>(OnTick);

			VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
		}
	}
}