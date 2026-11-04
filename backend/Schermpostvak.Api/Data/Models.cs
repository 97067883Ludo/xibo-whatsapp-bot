using System.ComponentModel.DataAnnotations;

namespace Schermpostvak.Api.Data;

public enum MediaType { Photo, Video }

public enum MediaStatus
{
    Downloading, // webhook received, file not fetched yet
    Pending,     // processed, waiting for a reviewer
    Approved,    // approved, waiting to be uploaded to Xibo
    Live,        // in one or more Xibo playlists
    Rejected,
    Expired,     // show-until passed
    Removed,     // taken off the screens by a reviewer
    Failed       // gave up after repeated errors
}

/// <summary>A WhatsApp chat (group or private) the bot number is part of.</summary>
public class Chat
{
    [Key] public string Jid { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsGroup { get; set; }
    /// <summary>New chats are enabled automatically; media from a disabled chat is ignored.</summary>
    public bool Enabled { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
}

public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WaMessageId { get; set; } = "";
    public string ChatJid { get; set; } = "";
    public Chat? Chat { get; set; }
    public string SenderJid { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string Caption { get; set; } = "";
    public MediaType Type { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.Downloading;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    // Files, relative to the storage root
    public string? FilePath { get; set; }
    public string? ThumbPath { get; set; }
    public string? Sha256 { get; set; }
    public double? DurationSeconds { get; set; }

    // Review
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? RejectReason { get; set; }
    public string? Screen { get; set; }
    /// <summary>Local time as entered in the dashboard (CMS time zone).</summary>
    public DateTime? ShowFrom { get; set; }
    public DateTime? ShowUntil { get; set; }
    public int? DisplaySeconds { get; set; }
    public int Rotation { get; set; }

    // Xibo
    public int? XiboMediaId { get; set; }
    /// <summary>Comma-separated widget ids, one per playlist the item was assigned to.</summary>
    public string? XiboWidgetIds { get; set; }

    // Processing
    public int Attempts { get; set; }
    public string? Error { get; set; }
}
