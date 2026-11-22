using System;
using System.Collections.Generic;
using System.IO;

namespace Flotsam.Multiplayer;

public sealed class BootstrapTransfer
{
    public const int ChunkBytes=48*1024,MaxBytes=5*1024*1024;
    private Packet? _header;
    private MemoryStream? _buffer;
    private int _next;
    public static IEnumerable<Packet> Split(Packet packet)
    {
        var bytes=Convert.FromBase64String(packet.bootstrap);
        if(bytes.Length>MaxBytes||bytes.Length==0)throw new InvalidDataException("世界快照大小无效");
        var id=Guid.NewGuid().ToString("N");
        yield return new Packet{type="bootstrapBegin",nonce=id,peer=packet.peer,ship=packet.ship,world=packet.world,chunkCount=(bytes.Length+ChunkBytes-1)/ChunkBytes,totalBytes=bytes.Length};
        for(var offset=0;offset<bytes.Length;offset+=ChunkBytes)yield return new Packet{type="bootstrapChunk",nonce=id,sequence=offset/ChunkBytes,bootstrap=Convert.ToBase64String(bytes,offset,Math.Min(ChunkBytes,bytes.Length-offset))};
    }
    public Packet? Receive(Packet packet)
    {
        if(packet.type=="bootstrapBegin")
        {
            if(packet.totalBytes<=0||packet.totalBytes>MaxBytes||packet.chunkCount!=(packet.totalBytes+ChunkBytes-1)/ChunkBytes||!Guid.TryParseExact(packet.nonce,"N",out _))throw new InvalidDataException("快照分块信息无效");
            _buffer?.Dispose();_buffer=new MemoryStream(packet.totalBytes);_header=packet;_next=0;return null;
        }
        if(_header==null||_buffer==null||packet.type!="bootstrapChunk"||packet.nonce!=_header.nonce||packet.sequence!=_next)throw new InvalidDataException("快照分块顺序无效");
        var expected=Math.Min(ChunkBytes,_header.totalBytes-(int)_buffer.Length);
        if(packet.bootstrap.Length>(ChunkBytes+2)/3*4)throw new InvalidDataException("快照分块过大");
        var bytes=Convert.FromBase64String(packet.bootstrap);if(bytes.Length!=expected)throw new InvalidDataException("快照分块长度无效");
        _buffer.Write(bytes,0,bytes.Length);_next++;
        if(_next<_header.chunkCount)return null;
        var completed=new Packet{type="bootstrap",peer=_header.peer,ship=_header.ship,world=_header.world,bootstrap=Convert.ToBase64String(_buffer.ToArray())};
        _buffer.Dispose();_buffer=null;_header=null;return completed;
    }
}
