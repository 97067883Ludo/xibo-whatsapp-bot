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

public class EvolutionOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Instance { get; set; } = "";
}

public class XiboOptions
{
    public string BaseUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public int DefaultDisplaySeconds { get; set; } = 10;
    public List<XiboScreen> Screens { get; set; } = [];
}

public class XiboScreen
{
    public string Name { get; set; } = "";
    public int PlaylistId { get; set; }
}
