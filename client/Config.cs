using System;
using System.IO;
using System.Text.Json;
using BfbbOnline.Shared;

namespace BfbbOnline.Client;

/// <summary>Launcher settings, kept in BFBB-Online.json next to the exe.</summary>
public sealed class Config
{
    public string Name { get; set; } = Environment.UserName;
    public string Color { get; set; } = "#ffd800";
    public string Server { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Proto.DefaultPort;
    public string Party { get; set; } = "bikini-bottom";
    /// <summary>Run the server inside the launcher (friends connect to this PC).</summary>
    public bool Host { get; set; }
    /// <summary>Host only: simulate occupied maps with headless games.</summary>
    public bool HostSims { get; set; } = true;
    public int HostMaxSims { get; set; } = 2;
    /// <summary>Host only: open the server port on the router with UPnP (friends on the internet).</summary>
    public bool HostUpnp { get; set; } = true;
    public string DolphinPath { get; set; } = "";
    public string DeluxeDir { get; set; } = "";

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "BFBB-Online.json");
    static readonly JsonSerializerOptions Jso = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Jso) ?? new Config();
        }
        catch (Exception) { }
        return new Config();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Jso)); } catch (Exception) { }
    }
}
