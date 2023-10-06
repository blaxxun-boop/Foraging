using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Foraging;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class Foraging : BaseUnityPlugin
{
	private const string ModName = "Foraging";
	private const string ModVersion = "1.0.6";
	private const string ModGUID = "org.bepinex.plugins.foraging";

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<Scope> foragingScope = null!;
	private static ConfigEntry<float> foragingYieldFactor = null!;
	private static ConfigEntry<int> respawnDisplayMinimumLevel = null!;
	public static ConfigEntry<int> massPickingRadius = null!;
	private static ConfigEntry<float> respawnSpeedMultiplier = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;
	private static ConfigEntry<int> experienceLoss = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0,
	}

	private enum Scope
	{
		Foraging,
		NoCrops,
		Everything,
	}

	private class ConfigurationManagerAttributes
	{
		public int? Order;
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	private static Skill foraging = null!;

	public void Awake()
	{
		foraging = new Skill("Foraging", "foraging.png");
		foraging.Description.English("Increases item yield for foraging and makes mushrooms and berries respawn quicker.");
		foraging.Name.German("Nahrungssuche");
		foraging.Description.German("Erhöht die Beute während der Nahrungssuche und lässt Beeren und Pilze schneller nachwachsen.");
		foraging.Configurable = false;

		int order = 0;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, new ConfigDescription("If on, the configuration is locked and can be changed by server admins only.", null, new ConfigurationManagerAttributes { Order = --order }));
		configSync.AddLockingConfigEntry(serverConfigLocked);
		foragingScope = config("2 - Foraging", "Foraging Scope", Scope.Foraging, new ConfigDescription("Foraging: Only applies to berries and mushrooms and the like.\nNo Crops: Applies to all pickables except for crops planted by a player.\nEverything: Applies to all pickables.", null, new ConfigurationManagerAttributes { Order = --order }));
		foragingYieldFactor = config("2 - Foraging", "Foraging Yield Factor", 2f, new ConfigDescription("Foraging yield factor at skill level 100.", new AcceptableValueRange<float>(1f, 5f), new ConfigurationManagerAttributes { Order = --order }));
		respawnDisplayMinimumLevel = config("2 - Foraging", "Minimum Level Respawn Display", 30, new ConfigDescription("Skill level required to see a timer when pickables will respawn. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false, Order = --order }));
		massPickingRadius = config("2 - Foraging", "Maximum Mass Picking Range", 10, new ConfigDescription("Mass picking radius at skill level 100 in meters.", new AcceptableValueRange<int>(0, 20), new ConfigurationManagerAttributes { Order = --order }));
		respawnSpeedMultiplier = config("2 - Foraging", "Multiplier for Respawn Speed", 2f, new ConfigDescription("Multiplier for the respawn speed at skill level 100.", new AcceptableValueRange<float>(1f, 10f), new ConfigurationManagerAttributes { Order = --order }));
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the foraging skill.", new AcceptableValueRange<float>(0.01f, 5f), new ConfigurationManagerAttributes { Order = --order }));
		experienceGainedFactor.SettingChanged += (_, _) => foraging.SkillGainFactor = experienceGainedFactor.Value;
		foraging.SkillGainFactor = experienceGainedFactor.Value;
		experienceLoss = config("3 - Other", "Skill Experience Loss", 0, new ConfigDescription("How much experience to lose in the foraging skill on death.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order }));
		experienceLoss.SettingChanged += (_, _) => foraging.SkillLoss = experienceLoss.Value;
		foraging.SkillLoss = experienceLoss.Value;

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	public static bool isForaging(Pickable pickable)
	{
		switch (foragingScope.Value)
		{
			case Scope.Foraging:
				return pickable.m_respawnTimeMinutes > 0 && pickable.m_itemPrefab.name != "Wood" && pickable.m_itemPrefab.name != "DragonEgg";

			case Scope.NoCrops:
				int itemLayer = LayerMask.NameToLayer("item");
				int plantLayer = LayerMask.NameToLayer("piece_nonsolid");
				int layer = pickable.gameObject.layer;
				return layer != plantLayer && (layer != itemLayer || pickable.m_amount <= 1);

			default:
				return true;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Update))]
	public class PlayerUpdate
	{
		private static void Postfix(Player __instance)
		{
			if (__instance == Player.m_localPlayer)
			{
				__instance.m_nview.GetZDO().Set("Foraging Skill Factor", __instance.GetSkillFactor(Skill.fromName("Foraging")));
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.Awake))]
	public class PlayerAwake
	{
		private static void Postfix(Player __instance)
		{
			__instance.m_nview.Register("Foraging IncreaseSkill", (long _, int factor) => __instance.RaiseSkill("Foraging", factor));
		}
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
	public class SaveSkillLevel
	{
		private static void Prefix(Pickable __instance)
		{
			if (__instance.m_nview.IsValid() && isForaging(__instance))
			{
				__instance.m_nview.GetZDO().Set("Foraging Skill Level", Player.m_localPlayer.GetSkillFactor("Foraging"));
			}
		}
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.RPC_Pick))]
	private static class IncreaseSkill
	{
		private static void Prefix(Pickable __instance, long sender, ref int? __state)
		{
			if (!__instance.m_nview.IsOwner() || __instance.m_picked)
			{
				__state = -1;
				return;
			}

			if (__instance.m_nview.IsValid() && isForaging(__instance))
			{
				if (Player.s_players.FirstOrDefault(p => p.m_nview.GetZDO().m_uid.UserID == sender) is { } player)
				{
					player.m_nview.InvokeRPC("Foraging IncreaseSkill", 1);

					if (Random.Range(0f, 1f) < player.m_nview.GetZDO().GetFloat("Foraging Skill Factor"))
					{
						__state = __instance.m_amount;

						int baseYield = Mathf.FloorToInt(foragingYieldFactor.Value);
						__instance.m_amount *= baseYield + (Random.Range(0f, 1f) < foragingYieldFactor.Value - baseYield ? 1 : 0);
					}
				}
			}
		}

		private static void Postfix(Pickable __instance, long sender, int? __state)
		{
			if (__instance.m_nview.IsValid() && __state != -1 && isForaging(__instance) && Player.s_players.FirstOrDefault(p => p.m_nview.GetZDO().m_uid.UserID == sender) is { } player)
			{
				long pickedTime = __instance.m_nview.GetZDO().GetLong("picked_time");
				__instance.m_nview.GetZDO().Set("picked_time", pickedTime - (long)(__instance.m_respawnTimeMinutes * TimeSpan.TicksPerMinute * (1 - 1 / respawnSpeedMultiplier.Value) * (double)player.m_nview.GetZDO().GetFloat("Foraging Skill Factor")));
			}
		}

		private static void Finalizer(Pickable __instance, int? __state)
		{
			if (__state is { } amount and >= 0)
			{
				__instance.m_amount = amount;
			}
		}
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
	private static class DisplayRespawn
	{
		private static void Postfix(Pickable __instance, ref string __result)
		{
			if (!__instance.m_nview.IsValid())
			{
				return;
			}

			if (__instance is { m_picked: true, m_respawnTimeMinutes: > 0 } && __instance.transform != __instance.m_hideWhenPicked.transform.parent && __instance.gameObject != __instance.m_hideWhenPicked && respawnDisplayMinimumLevel.Value > 0 && Player.m_localPlayer.GetSkillFactor("Foraging") >= respawnDisplayMinimumLevel.Value / 100f)
			{
				DateTime pickedTime = new(__instance.m_nview.GetZDO().GetLong("picked_time"));
				TimeSpan respawnIn = TimeSpan.FromMinutes(__instance.m_respawnTimeMinutes) - (ZNet.instance.GetTime() - pickedTime);
				__result += $"\nRespawn in: {respawnIn - new TimeSpan(respawnIn.Ticks % TimeSpan.TicksPerSecond):c}";
			}
		}
	}
}
