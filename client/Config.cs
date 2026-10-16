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
    /// <summary>Watch teammates' story cutscenes with them and get "watch along?" prompts for their conversations.</summary>
    public bool WatchAlong { get; set; } = true;
    /// <summary>Screen the game window opens on (Windows device name, "" = Dolphin's choice).</summary>
    public string Monitor { get; set; } = "";
    /// <summary>That screen's bounds when it was picked ("x,y,w,h"): finds it again after Windows renumbers displays.</summary>
    public string MonitorBounds { get; set; } = "";
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
