using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace WeaponPaints
{
	public class Additional
	{
		[JsonPropertyName("KnifeEnabled")]
		public bool KnifeEnabled { get; set; } = true;

		[JsonPropertyName("GloveEnabled")]
		public bool GloveEnabled { get; set; } = true;

		[JsonPropertyName("MusicEnabled")]
		public bool MusicEnabled { get; set; } = true;

		[JsonPropertyName("AgentEnabled")]
		public bool AgentEnabled { get; set; } = true;

		[JsonPropertyName("SkinEnabled")]
		public bool SkinEnabled { get; set; } = true;

		[JsonPropertyName("PinsEnabled")]
		public bool PinsEnabled { get; set; } = true;

		[JsonPropertyName("CommandWpEnabled")]
		public bool CommandWpEnabled { get; set; } = true;

		[JsonPropertyName("CommandKillEnabled")]
		public bool CommandKillEnabled { get; set; } = true;

		[JsonPropertyName("CommandKnife")]
		public List<string> CommandKnife { get; set; } = ["knife"];

		[JsonPropertyName("CommandMusic")]
		public List<string> CommandMusic { get; set; } = ["music"];
		
		[JsonPropertyName("CommandPin")]
		public List<string> CommandPin { get; set; } = ["pin", "pins", "coin", "coins"];

		[JsonPropertyName("CommandGlove")]
		public List<string> CommandGlove { get; set; } = ["gloves"];

		[JsonPropertyName("CommandAgent")]
		public List<string> CommandAgent { get; set; } = ["agents"];
		
		[JsonPropertyName("CommandStattrak")]
		public List<string> CommandStattrak { get; set; } = ["stattrak", "st"];

		[JsonPropertyName("CommandSkin")]
		public List<string> CommandSkin { get; set; } = ["ws"];

		[JsonPropertyName("CommandSkinSelection")]
		public List<string> CommandSkinSelection { get; set; } = ["skins"];

		[JsonPropertyName("CommandRefresh")]
		public List<string> CommandRefresh { get; set; } = ["wp"];

		[JsonPropertyName("CommandKill")]
		public List<string> CommandKill { get; set; } = ["kill"];

		[JsonPropertyName("GiveRandomKnife")]
		public bool GiveRandomKnife { get; set; } = false;

		[JsonPropertyName("GiveRandomSkin")]
		public bool GiveRandomSkin { get; set; } = false;

		[JsonPropertyName("ShowSkinImage")]
		public bool ShowSkinImage { get; set; } = true;
	}

	public class WeaponPaintsConfig : BasePluginConfig
	{
        [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 11;

        [JsonPropertyName("SkinsLanguage")]
		public string SkinsLanguage { get; set; } = "en";

		/// <summary>
		/// Flag de permiso requerido para que se carguen las skins del jugador.
		/// Vacío = sin restricción (comportamiento original: carga para todos).
		///
		/// Se consulta contra el AdminManager de CounterStrikeSharp, donde
		/// HaporePermissions deja los flags ya resueltos del jugador al conectar.
		/// Sirve para que las skins sean un beneficio VIP sin tener que borrar de
		/// la base lo que el jugador configuró: al vencer el VIP simplemente
		/// dejan de cargarse, y si renueva vuelve a tener su loadout intacto.
		/// </summary>
		[JsonPropertyName("SkinsPermissionFlag")]
		public string SkinsPermissionFlag { get; set; } = "";

		[JsonPropertyName("DatabaseHost")]
		public string DatabaseHost { get; set; } = "";

		[JsonPropertyName("DatabasePort")]
		public int DatabasePort { get; set; } = 3306;

		[JsonPropertyName("DatabaseUser")]
		public string DatabaseUser { get; set; } = "";

		[JsonPropertyName("DatabasePassword")]
		public string DatabasePassword { get; set; } = "";

		[JsonPropertyName("DatabaseName")]
		public string DatabaseName { get; set; } = "";

		[JsonPropertyName("CmdRefreshCooldownSeconds")]
		public int CmdRefreshCooldownSeconds { get; set; } = 3;

		[JsonPropertyName("Website")]
		public string Website { get; set; } = "example.com/skins";

		[JsonPropertyName("Additional")]
		public Additional Additional { get; set; } = new();
		
		[JsonPropertyName("MenuType")]
		public string MenuType { get; set; } = "selectable";
	}
}