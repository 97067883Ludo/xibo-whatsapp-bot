namespace Schermpostvak.Api;

public class StorageOptions
{
    public string Root { get; set; } = "data/media";
    public int RejectedRetentionDays { get; set; } = 14;
}

public class WebhookOptions
{
    public string Token { get; set; } = "";
}

public class WahaOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Session { get; set; } = "default";
}

public class XiboOptions
{
    public string BaseUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public int DefaultDisplaySeconds { get; set; } = 10;
    /// <summary>Library tag the dynamic playlists on the screens filter on.</summary>
    public string Tag { get; set; } = "onscreen";
}
