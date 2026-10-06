using System;
using System.Linq;

namespace TVHeadEnd.Configuration;

public sealed class NativeServerConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "TVHeadend";
    public string Host { get; set; } = "localhost";
    public int HtspPort { get; set; } = 9982;
    public int HttpPort { get; set; } = 9981;
    public bool UseHttps { get; set; }
    public string WebRoot { get; set; } = "/";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Profile { get; set; } = "";
    public string StreamingMethod { get; set; } = StreamingMethods.Htsp;
    public string TimeZoneId { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Native server ID must contain only ASCII letters, digits or hyphens.");
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown
            || HtspPort is < 1 or > 65535 || HttpPort is < 1 or > 65535)
            throw new ArgumentException("Configure a hostname or IP address and valid HTSP and HTTP ports.");
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            throw new ArgumentException("Configure the TVHeadend username and password.");
    }
}
