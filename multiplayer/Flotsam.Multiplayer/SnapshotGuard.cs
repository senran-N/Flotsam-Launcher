using System;
using System.IO;
using System.IO.Compression;

namespace Flotsam.Multiplayer;
public static class SnapshotGuard
{
    public static void Validate(byte[] bytes)
    {
        if(bytes.Length<2||bytes.Length>5*1024*1024||bytes[0]!=0x1f||bytes[1]!=0x8b)throw new InvalidDataException("世界快照格式无效");
        using var input=new MemoryStream(bytes);using var gzip=new GZipStream(input,CompressionMode.Decompress);
        var buffer=new byte[8192];var total=0;var first=true;
        while(true)
        {
            var count=gzip.Read(buffer,0,buffer.Length);if(count==0)break;
            if(first&&buffer[0]!='{')throw new InvalidDataException("只支持 JSON 世界快照");
            first=false;total+=count;if(total>32*1024*1024)throw new InvalidDataException("世界快照解压后超限");
        }
        if(first)throw new InvalidDataException("空世界快照");
    }
}
