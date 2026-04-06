using EVEStandard.Models;
using EVEStandard.Models.API;

namespace SLH.Services;

/// <summary>
/// Builds a merged view of personal, corporation, and alliance contacts (max standing per entity id) for tinting local.
/// </summary>
public sealed class ContactStandingIndex : IContactStandingCache
{
    private readonly EveConnectionService _eve;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private Dictionary<long, float> _byCharacter = new();
    private Dictionary<long, float> _byCorporation = new();
    private Dictionary<long, float> _byAlliance = new();
    private DateTimeOffset _validUntil;
    private long _selfCorporationId;
    private long _selfAllianceId;

    public ContactStandingIndex(EveConnectionService eve) => _eve = eve;

    public void Clear()
    {
        lock (_sync)
        {
            _byCharacter = new Dictionary<long, float>();
            _byCorporation = new Dictionary<long, float>();
            _byAlliance = new Dictionary<long, float>();
            _validUntil = default;
            _selfCorporationId = 0;
            _selfAllianceId = 0;
        }
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_eve.IsAuthenticated)
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_validUntil > now)
                return;
        }

        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            lock (_sync)
            {
                if (_validUntil > now)
                    return;
            }

            await _eve.EnsureFreshAccessAsync(cancellationToken).ConfigureAwait(false);
            if (!_eve.IsAuthenticated)
                return;

            var byCharacter = new Dictionary<long, float>();
            var byCorporation = new Dictionary<long, float>();
            var byAlliance = new Dictionary<long, float>();

            await AppendCharacterContactsAsync(byCharacter, byCorporation, byAlliance, cancellationToken)
                .ConfigureAwait(false);

            var charInfo = await _eve.Api.Character.GetCharacterPublicInfoAsync(_eve.CharacterId!.Value)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var corpId = charInfo.Model?.CorporationId ?? 0L;
            var selfAlliance = 0L;
            if (corpId > 0)
            {
                await AppendCorporationContactsSafeAsync(corpId, byCharacter, byCorporation, byAlliance,
                        cancellationToken)
                    .ConfigureAwait(false);

                long? allianceId = null;
                try
                {
                    var corpInfo = await _eve.Api.Corporation.GetCorporationInfoAsync(corpId)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                    var aid = corpInfo.Model?.AllianceId;
                    if (aid is > 0)
                        allianceId = aid;
                }
                catch
                {
                    // ignore
                }

                if (allianceId is > 0)
                {
                    selfAlliance = allianceId.Value;
                    await AppendAllianceContactsSafeAsync(allianceId.Value, byCharacter, byCorporation, byAlliance,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            lock (_sync)
            {
                _byCharacter = byCharacter;
                _byCorporation = byCorporation;
                _byAlliance = byAlliance;
                _selfCorporationId = corpId;
                _selfAllianceId = selfAlliance;
                _validUntil = DateTimeOffset.UtcNow.Add(_ttl);
            }
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    /// <summary>
    /// Standing from a direct <b>character</b> contact only, plus +10 if this is the logged-in character.
    /// Does not use corporation/alliance contacts (those need corp id from affiliation).
    /// </summary>
    public float GetQuickStandingForCharacter(long characterId)
    {
        const float sameCharStanding = 10f;
        lock (_sync)
        {
            _byCharacter.TryGetValue(characterId, out var c);
            if (_eve.CharacterId is { } me && characterId == me)
                return Math.Max(c, sameCharStanding);
            return c;
        }
    }

    public float GetEffectiveStanding(long characterId, long corporationId, long? allianceId)
    {
        const float sameOrgStanding = 10f;
        lock (_sync)
        {
            _byCharacter.TryGetValue(characterId, out var c);
            _byCorporation.TryGetValue(corporationId, out var co);
            var a = 0f;
            if (allianceId is > 0 && _byAlliance.TryGetValue(allianceId.Value, out var av))
                a = av;
            var fromContacts = Math.Max(c, Math.Max(co, a));

            var selfChar = _eve.CharacterId;
            if (selfChar is { } me && characterId == me)
                return Math.Max(fromContacts, sameOrgStanding);
            if (corporationId > 0 && corporationId == _selfCorporationId)
                return Math.Max(fromContacts, sameOrgStanding);
            if (allianceId is > 0 && _selfAllianceId > 0 && allianceId.Value == _selfAllianceId)
                return Math.Max(fromContacts, sameOrgStanding);

            return fromContacts;
        }
    }

    private static void MergeStanding(Dictionary<long, float> map, long id, float standing)
    {
        if (id <= 0)
            return;
        if (!map.TryGetValue(id, out var cur) || standing > cur)
            map[id] = standing;
    }

    private static void AddContact(string? contactType, long contactId, float standing,
        Dictionary<long, float> byCharacter, Dictionary<long, float> byCorporation, Dictionary<long, float> byAlliance)
    {
        if (string.IsNullOrEmpty(contactType))
            return;
        switch (contactType.ToLowerInvariant())
        {
            case "character":
                MergeStanding(byCharacter, contactId, standing);
                break;
            case "corporation":
                MergeStanding(byCorporation, contactId, standing);
                break;
            case "alliance":
                MergeStanding(byAlliance, contactId, standing);
                break;
        }
    }

    private async Task AppendCharacterContactsAsync(
        Dictionary<long, float> byCharacter,
        Dictionary<long, float> byCorporation,
        Dictionary<long, float> byAlliance,
        CancellationToken cancellationToken)
    {
        var auth = _eve.Auth;
        for (var page = 1; ; page++)
        {
            ESIModelDTO<List<CharacterContact>> dto =
                await _eve.Api.Contacts.GetContactsAsync(auth, page).WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

            var list = dto.Model;
            if (list == null || list.Count == 0)
                break;

            foreach (var x in list)
                AddContact(x.ContactType, x.ContactId, x.Standing, byCharacter, byCorporation, byAlliance);

            if (dto.MaxPages > 0 && page >= dto.MaxPages)
                break;
            if (list.Count < 250)
                break;
        }
    }

    private async Task AppendCorporationContactsSafeAsync(
        long corporationId,
        Dictionary<long, float> byCharacter,
        Dictionary<long, float> byCorporation,
        Dictionary<long, float> byAlliance,
        CancellationToken cancellationToken)
    {
        try
        {
            var auth = _eve.Auth;
            for (var page = 1; ; page++)
            {
                var dto = await _eve.Api.Contacts.GetCorporationContactsAsync(auth, corporationId, page)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);

                var list = dto.Model;
                if (list == null || list.Count == 0)
                    break;

                foreach (var x in list)
                    AddContact(x.ContactType, x.ContactId, x.Standing, byCharacter, byCorporation, byAlliance);

                if (dto.MaxPages > 0 && page >= dto.MaxPages)
                    break;
                if (list.Count < 250)
                    break;
            }
        }
        catch
        {
            // Missing role or scope — use personal contacts only
        }
    }

    private async Task AppendAllianceContactsSafeAsync(
        long allianceId,
        Dictionary<long, float> byCharacter,
        Dictionary<long, float> byCorporation,
        Dictionary<long, float> byAlliance,
        CancellationToken cancellationToken)
    {
        try
        {
            var auth = _eve.Auth;
            for (var page = 1; ; page++)
            {
                var dto = await _eve.Api.Contacts.GetAllianceContactsAsync(auth, allianceId, page)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);

                var list = dto.Model;
                if (list == null || list.Count == 0)
                    break;

                foreach (var x in list)
                    AddContact(x.ContactType, x.ContactId, x.Standing, byCharacter, byCorporation, byAlliance);

                if (dto.MaxPages > 0 && page >= dto.MaxPages)
                    break;
                if (list.Count < 250)
                    break;
            }
        }
        catch
        {
            // Alliance contacts often require director-level roles
        }
    }
}
