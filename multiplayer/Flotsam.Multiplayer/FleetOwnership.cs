using System;
using System.Collections.Generic;

namespace Flotsam.Multiplayer;

internal static class FleetOwnership
{
    private static readonly Dictionary<Community,string> Bindings=new Dictionary<Community,string>();
    internal static bool Verified(Community ship)=>Bindings.ContainsKey(ship);
    internal static string SavedOwner(Community ship)=>Bindings.TryGetValue(ship,out var owner)?owner:"";
    internal static string Owner(Community ship,string fallback)=>Bindings.TryGetValue(ship,out var owner)?owner:fallback;
    internal static void Bind(Community ship,string owner){if(!Guid.TryParseExact(owner,"N",out _))return;Bindings[ship]=owner;FleetEngineBundle.RebindShipId(ship,owner);}
    internal static void Reset()=>Bindings.Clear();
}
internal sealed class ShipOwnershipRequest
{
    internal long Id;
    internal string Peer="",Owner="",Player="";
    internal Community Ship=null!;
}
