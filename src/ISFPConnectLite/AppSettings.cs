using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISFPConnectLite;

public class AppSettings
{
    public string Callsign { get; set; } = "";
    public string Cid { get; set; } = "";
    public string Password { get; set; } = "";
    public string RealName { get; set; } = "";
    public string AircraftIcao { get; set; } = "";
    public string AirlineIcao { get; set; } = "";
    public string XplaneDir { get; set; } = "";
    public bool RememberPassword { get; set; } = true;

    [JsonIgnore]
    public static string FilePath
    {
        get
        {
            // 软件根目录（exe 所在目录）下的 config.json
            // AppContext.BaseDirectory 在单文件发布下也指向 exe 真实目录
            return Path.Combine(AppContext.BaseDirectory, "config.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
