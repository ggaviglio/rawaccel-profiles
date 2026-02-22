using System;

namespace MouseApp.Models;

public class AppSettings
{
    public string RawAccelDirectory { get; set; } = string.Empty;
    public string WriterExePath { get; set; } = string.Empty;
    public bool EnableRegistryBridge { get; set; } = false;
    public bool RunAtStartup { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
}
