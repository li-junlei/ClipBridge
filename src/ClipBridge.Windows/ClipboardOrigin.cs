using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge;
static class ClipboardOrigin
{
    public const string Format="ClipBridge.Origin.v1";
    public static DataObject RemoteText(string text,string origin,string eventId){var data=new DataObject();data.SetText(text,TextDataFormat.UnicodeText);data.SetData(Format,JsonSerializer.Serialize(new{origin,eventId,hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}));return data;}
    public static bool IsRemote(IDataObject? data){try{if(data is null||!data.GetDataPresent(Format)||!data.GetDataPresent(DataFormats.UnicodeText))return false;using var json=JsonDocument.Parse((string)data.GetData(Format)!);var value=json.RootElement;Wire.Id(value.GetProperty("origin").GetString()!);Wire.Id(value.GetProperty("eventId").GetString()!);string text=(string)data.GetData(DataFormats.UnicodeText)!;return value.GetProperty("hash").GetString()==Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));}catch{return false;}}
}
