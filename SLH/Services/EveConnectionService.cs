using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EVEStandard;
using EVEStandard.Enumerations;
using EVEStandard.Models.API;
using EVEStandard.Models.SSO;
using Microsoft.Extensions.Configuration;
using SLH.Models;

namespace SLH.Services;

public sealed class EveConnectionService
{
    private readonly IConfiguration _configuration;
    private readonly SecureSessionStore _sessionStore;

    /// <summary>When set, cleared on <see cref="Logout"/> (contact standing cache).</summary>
    public IContactStandingCache? ContactStandingCache { get; set; }
    private readonly TimeSpan _httpTimeout = TimeSpan.FromMinutes(2);

    private EVEStandardAPI? _api;
    private SSOv2? _sso;
    private AccessTokenDetails? _access;
    private AuthDTO? _authDto;
    private CharacterDetails? _characterDetails;

    public EveConnectionService(IConfiguration configuration, SecureSessionStore sessionStore)
    {
        _configuration = configuration;
        _sessionStore = sessionStore;
    }

    public bool IsAuthenticated => _access != null && _authDto != null
                                   && _access.ExpiresUtc > DateTime.UtcNow.AddMinutes(1);

    public long? CharacterId => _characterDetails?.CharacterId;
    public string CharacterName => _characterDetails?.CharacterName ?? "";
    public string PortraitUrl =>
        CharacterId is { } id && id > 0
            ? $"https://images.evetech.net/characters/{id}/portrait?tenant=tranquility&size=64"
            : "";

    public EVEStandardAPI Api => _api ?? throw new InvalidOperationException("ESI not initialized.");
    public AuthDTO Auth => _authDto ?? throw new InvalidOperationException("Not logged in.");

    public void InitializeApi()
    {
        if (_api != null)
            return;
        var ua = _configuration["UserAgent"] ?? "SLH/0.1";
        _api = new EVEStandardAPI(ua, DataSource.Tranquility, CompatibilityDate.v2025_12_16, TimeSpan.FromSeconds(120));
    }

    public async Task RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        InitializeApi();
        var stored = _sessionStore.Load();
        if (stored == null || string.IsNullOrWhiteSpace(stored.RefreshToken))
            return;

        if (string.IsNullOrWhiteSpace(EsiBuildSettings.EveClientId))
            return;

        _sso = new SSOv2(DataSource.Tranquility, BuildCallbackUri(EsiBuildSettings.CallbackPort), EsiBuildSettings.EveClientId);
        var refreshed = await _sso.GetNewPKCEAccessAndRefreshTokenAsync(stored.RefreshToken).WaitAsync(cancellationToken);
        await ApplyTokenBundleAsync(refreshed, stored, cancellationToken).ConfigureAwait(false);
        _sessionStore.Save(stored);
    }

    public async Task LoginWithBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(EsiBuildSettings.EveClientId))
            throw new InvalidOperationException("Set EsiBuildSettings.EveClientId in source and rebuild.");

        InitializeApi();
        var callbackUri = BuildCallbackUri(EsiBuildSettings.CallbackPort);
        _sso = new SSOv2(DataSource.Tranquility, callbackUri, EsiBuildSettings.EveClientId);

        var state = CreateStateToken();
        var (verifier, challenge) = Pkce.CreateChallenge();
        // Add matching scopes on https://developers.eveonline.com for this application.
        var scopes = new List<string>
        {
            Scopes.ESI_LOCATION_READ_LOCATION_1,
            Scopes.ESI_LOCATION_READ_ONLINE_1,
            Scopes.ESI_LOCATION_READ_SHIP_TYPE_1,
            Scopes.ESI_CHARACTERS_READ_CONTACTS_1,
            Scopes.ESI_CORPORATIONS_READ_CONTACTS_1,
            Scopes.ESI_ALLIANCE_READ_CONTACTS_1
        };
        var authUrl = _sso.AuthorizeToSSOPKCEUri(state, challenge, scopes);

        using var listener = new HttpListener();
        listener.Prefixes.Add(callbackUri);
        listener.Start();

        try
        {
            Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(_httpTimeout, cancellationToken));
            if (completed != contextTask)
                throw new TimeoutException("Login timed out waiting for EVE redirect.");

            var context = await contextTask.WaitAsync(cancellationToken);
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var err = query["error"];

            const string responseHtml =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>SLH</title></head>" +
                "<body style=\"font-family:system-ui;background:#0d1117;color:#e6edf3;text-align:center;padding:2rem;\">" +
                "<p>You can close this window and return to SLH.</p></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await context.Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            context.Response.OutputStream.Close();

            if (!string.IsNullOrEmpty(err))
                throw new InvalidOperationException($"SSO error: {err} — {query["error_description"]}");

            if (string.IsNullOrEmpty(code) || returnedState != state)
                throw new InvalidOperationException("Invalid SSO callback (missing code or state mismatch).");

            var tokenResponse = await _sso.VerifyAuthorizationForPKCEAuthAsync(code, verifier).WaitAsync(cancellationToken);
            var stored = new StoredSession();
            await ApplyTokenBundleAsync(tokenResponse, stored, cancellationToken).ConfigureAwait(false);
            _sessionStore.Save(stored);
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Logout()
    {
        ContactStandingCache?.Clear();
        _sessionStore.Clear();
        _access = null;
        _authDto = null;
        _characterDetails = null;
    }

    public async Task EnsureFreshAccessAsync(CancellationToken cancellationToken = default)
    {
        if (_sso == null || _authDto?.AccessToken == null)
            return;
        if (_access!.ExpiresUtc > DateTime.UtcNow.AddMinutes(2))
            return;

        if (string.IsNullOrWhiteSpace(EsiBuildSettings.EveClientId))
            return;

        var refresh = _authDto.AccessToken.RefreshToken;
        if (string.IsNullOrEmpty(refresh))
            return;

        _sso = new SSOv2(DataSource.Tranquility, BuildCallbackUri(EsiBuildSettings.CallbackPort), EsiBuildSettings.EveClientId);
        var refreshed = await _sso.GetNewPKCEAccessAndRefreshTokenAsync(refresh).WaitAsync(cancellationToken);
        var stored = _sessionStore.Load() ?? new StoredSession
        {
            CharacterId = _authDto.CharacterId,
            CharacterName = CharacterName
        };
        await ApplyTokenBundleAsync(refreshed, stored, cancellationToken).ConfigureAwait(false);
        _sessionStore.Save(stored);
    }

    private async Task ApplyTokenBundleAsync(AccessTokenDetails details, StoredSession stored, CancellationToken cancellationToken)
    {
        _access = details;
        var sso = _sso ?? throw new InvalidOperationException("SSO not initialized.");
        _characterDetails = await sso.GetCharacterDetailsAsync(details.AccessToken).WaitAsync(cancellationToken);

        var scopesJoined = string.Join(' ', _characterDetails.Scopes ?? new List<string>());
        _authDto = new AuthDTO
        {
            CharacterId = _characterDetails.CharacterId,
            AccessToken = details,
            Scopes = " " + scopesJoined + " "
        };

        stored.CharacterId = _characterDetails.CharacterId;
        stored.CharacterName = _characterDetails.CharacterName;
        stored.RefreshToken = details.RefreshToken ?? stored.RefreshToken;
        stored.ScopesJoined = scopesJoined;
    }

    private string BuildCallbackUri(int port) => $"http://localhost:{port}/callback/";

    private static string CreateStateToken()
    {
        Span<byte> stateBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(stateBytes);
        return Convert.ToHexString(stateBytes);
    }
}
