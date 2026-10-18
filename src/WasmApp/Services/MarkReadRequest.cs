namespace WasmApp.Services;

/// <summary>Whether the reader has read a post.</summary>
public enum ReadState
{
    Unread,
    Read,
}

/// <summary>
/// The one place the mark-read request URL is built. Three callers send it:
/// <see cref="FeedClient"/> for in-app clicks, the outbox replay, and the
/// capture-phase link handler in app.js, which reads it off the link's
/// <c>data-mark-read-url</c> attribute. It must be absolute (the API origin),
/// not a path on the frontend origin: the session cookie lives on the API host,
/// so a path-relative request would reach the frontend's old proxy route with
/// no identity and the write would be silently dropped.
/// </summary>
public static class MarkReadRequest
{
    public static string Url(string apiBaseUrl, string itemId, ReadState state)
    {
        var isRead = state == ReadState.Read ? "true" : "false";
        return $"{apiBaseUrl}api/item/markAsRead?itemId={itemId}&isRead={isRead}";
    }

    public static ReadState StateOf(bool isRead) => isRead ? ReadState.Read : ReadState.Unread;
}
