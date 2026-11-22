using System;
using System.Collections.Generic;
using System.Linq;

namespace Flotsam.Multiplayer;

// Authoritative requests: a response names a particular request, never merely a ship.
public sealed class DiplomacyRequests
{
    private readonly List<AllianceRequestState> _pending = new List<AllianceRequestState>();
    private long _next;
    public AllianceRequestState[] Capture(float now)
    {
        _pending.RemoveAll(r => r.expiresAt <= now);
        return _pending.ToArray();
    }
    public string? Request(string from, string to, string owner, float now)
    {
        Capture(now);
        if (from == to || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return "结盟双方无效";
        if (_pending.Any(r => r.fromShip == from && r.toShip == to)) return "已发送申请，等待对方决定";
        if (_pending.Count >= 64) return "待处理申请过多，请稍后再试";
        _pending.Add(new AllianceRequestState { id = ++_next, fromShip = from, toShip = to, fromOwner = owner, expiresAt = now + 90f });
        return null;
    }
    public string? Respond(long id, string recipient, string sender, float now)
    {
        Capture(now);
        var request = _pending.FirstOrDefault(r => r.id == id);
        if (request == null) return "申请已过期或已处理";
        if (request.toShip != recipient || request.fromShip != sender) return "只有收到申请的船主可以处理此申请";
        _pending.Remove(request);
        return null;
    }
    public void RemoveBetween(string a, string b) => _pending.RemoveAll(r => (r.fromShip == a && r.toShip == b) || (r.fromShip == b && r.toShip == a));
    public void Clear() => _pending.Clear();
}
