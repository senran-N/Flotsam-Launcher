using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Flotsam.Multiplayer;

// A nickname is presentation. Only possession of the local private key can reclaim a ship.
public sealed class PlayerIdentity : IDisposable
{
    private readonly RSACryptoServiceProvider _key;
    public string PublicKey=>Convert.ToBase64String(_key.ExportCspBlob(false));
    public PlayerIdentity(string path)
    {
        _key=new RSACryptoServiceProvider(2048){PersistKeyInCsp=false};
        if(File.Exists(path))_key.ImportCspBlob(File.ReadAllBytes(path));
        else{_key.KeySize=2048;Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,_key.ExportCspBlob(true));}
    }
    public string Sign(string nonce,string peer,string fingerprint,string ticket)=>Convert.ToBase64String(_key.SignData(Message(nonce,peer,fingerprint,ticket),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
    private static byte[] Message(string n,string p,string f,string t)=>JsonCodec.Encode(new[]{"fleet-identity-v1",n,p,f,t});
    public static string Verify(string key,string signature,string nonce,string peer,string fingerprint,string ticket)
    {
        using var rsa=new RSACryptoServiceProvider(){PersistKeyInCsp=false};rsa.ImportCspBlob(Convert.FromBase64String(key));
        if(rsa.KeySize<2048||rsa.KeySize>4096||!rsa.VerifyData(Message(nonce,peer,fingerprint,ticket),Convert.FromBase64String(signature),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new InvalidDataException("玩家身份签名无效");
        using var sha=SHA256.Create();var hash=sha.ComputeHash(rsa.ExportCspBlob(false));var identity=new byte[16];Array.Copy(hash,identity,16);return new Guid(identity).ToString("N");
    }
    public void Dispose()=>_key.Dispose();
}
