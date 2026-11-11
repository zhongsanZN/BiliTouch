namespace BiliTouch.Services;

/// <summary>A request belongs to one login lifetime, including logout and re-login as the same user.</summary>
internal readonly struct SessionAccount
{
    private readonly SessionManager? _session;
    private readonly long _version;
    private readonly string? _userId;
    private readonly string? _sessionData;

    internal SessionAccount(SessionManager session)
    {
        _session = session;
        _version = session.AccountVersion;
        _userId = session.Current.DedeUserID;
        _sessionData = session.Current.SessData;
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(_sessionData);
    public bool IsCurrent => _session != null && _version == _session.AccountVersion
        && _userId == _session.Current.DedeUserID && _sessionData == _session.Current.SessData;
}
