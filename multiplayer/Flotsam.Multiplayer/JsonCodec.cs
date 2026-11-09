using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Flotsam.Multiplayer;
public static class JsonCodec
{
    public static byte[] EncodeRuntime(object value){using var stream=new MemoryStream();new DataContractJsonSerializer(value.GetType()).WriteObject(stream,value);return stream.ToArray();}
    public static object DecodeRuntime(System.Type type,byte[] bytes){using var stream=new MemoryStream(bytes);return new DataContractJsonSerializer(type).ReadObject(stream)!;}
    public static byte[] Encode<T>(T value){using var stream=new MemoryStream();new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings{MaxItemsInObjectGraph=200000}).WriteObject(stream,value);return stream.ToArray();}
    public static T Decode<T>(byte[] bytes){using var stream=new MemoryStream(bytes);return (T)new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings{MaxItemsInObjectGraph=200000}).ReadObject(stream)!;}
    public static string Text<T>(T value)=>Encoding.UTF8.GetString(Encode(value));
}
