using System;

namespace Flotsam.Multiplayer;

/// <summary>
/// Recalculates the native navigator paths of the secondary owner's residents after an owner move
/// or anchor rebase. The pinned GraphManager.RefreshNavigatorPaths body is empty, so this calls
/// the native NavigatorPathBase.Recalculate on each live owned navigator instead of reimplementing
/// the pathfinder. It does not create/destroy graph nodes.
/// </summary>
public static class FleetPathRefresh
{
    public static int Refresh(Community owner)
    {
        if (owner == null) return 0;
        var refreshed = 0;
        foreach (var agent in owner.Agents)
        {
            if (agent == null) continue;
            try
            {
                var navigator = agent.ReturnNavigator();
                if (navigator?.Path == null) continue;
                navigator.Path.Recalculate();
                refreshed++;
            }
            catch (Exception e) { Plugin.Log("Fleet path refresh skipped: " + e.GetType().Name); }
        }
        return refreshed;
    }
}
