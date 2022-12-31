using HarmonyLib;
using UnityEngine;
using SkillManager;

namespace Foraging;

public static class MassHarvest
{
	[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
	private class HarvestNearbyPickables
	{
		private static bool isPicked = false;
		
		[HarmonyPriority(Priority.LowerThanNormal)]
		private static void Postfix(Pickable __instance)
		{
			if (isPicked || Foraging.massPickingRadius.Value == 0 || __instance.m_respawnTimeMinutes == 0 || __instance.m_itemPrefab.name == "Wood")
			{
				return;
			}
			
			isPicked = true;

			int plantMask = LayerMask.GetMask("item", "Default_small");

			// ReSharper disable once Unity.PreferNonAllocApi
			foreach (Collider collider in Physics.OverlapSphere(__instance.transform.position, Player.m_localPlayer.GetSkillFactor("Foraging") * Foraging.massPickingRadius.Value, plantMask))
			{
				if ((collider.GetComponent<Pickable>() ?? collider.transform.parent?.GetComponent<Pickable>()) is { } pickable && pickable != __instance && pickable.m_respawnTimeMinutes > 0 && pickable.m_itemPrefab.name != "Wood" && !pickable.m_picked)
				{
					pickable.Interact(Player.m_localPlayer, false, false);
				}
			}

			isPicked = false;
		}
	}
}
