using System.Text.Json;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Tenancy;

namespace NexaVerify.Application.Tenancy;

public interface IClientSettingsService
{
    /// <summary>All settings of a client (defaults merged with overrides). <paramref name="groups"/> optionally limits the groups returned.</summary>
    Task<Result<IReadOnlyList<SettingDto>>> GetAsync(Guid clientId, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<SettingDto>>> UpdateAsync(Guid clientId, UpdateSettingsRequest request, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken);

    /// <summary>Typed snapshot for other modules (face engine, rate limiter, license rules).</summary>
    Task<ClientSettingsSnapshot> GetEffectiveAsync(Guid clientId, CancellationToken cancellationToken);
}

public sealed class ClientSettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public ClientSettingsSnapshot(IReadOnlyDictionary<string, JsonElement> values)
    {
        _values = values;
    }

    public int Int(string key) => _values[key].GetInt32();

    public decimal Decimal(string key) => _values[key].GetDecimal();

    public bool Bool(string key) => _values[key].GetBoolean();

    public IReadOnlyList<string> List(string key) => _values[key].EnumerateArray().Select(e => e.GetString()!).ToList();
}

/// <summary>
/// Reads and writes per-client settings. Anyone can read their own; writing is limited per key: platform-managed keys need
/// <c>clients.settings</c> (platform), client-managed keys need the key's permission (or platform's <c>clients.settings</c>).
/// Values are validated against the catalogue (type, bounds, list syntax) and every change is audited.
/// </summary>
public sealed class ClientSettingsService : IClientSettingsService
{
    private readonly IClientSettingRepository _settings;
    private readonly IClientRepository _clients;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;

    public ClientSettingsService(
        IClientSettingRepository settings,
        IClientRepository clients,
        ICurrentUser currentUser,
        IPermissionResolver permissions,
        IAuditService audit,
        IUnitOfWork unitOfWork)
    {
        _settings = settings;
        _clients = clients;
        _currentUser = currentUser;
        _permissions = permissions;
        _audit = audit;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<SettingDto>>> GetAsync(Guid clientId, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken) is null)
        {
            return Error.NotFound();
        }

        var overrides = (await _settings.GetAllAsync(clientId, cancellationToken)).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var granted = await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken);
        return Result<IReadOnlyList<SettingDto>>.Success(SettingCatalog.All
            .Where(d => groups is null || groups.Contains(d.Group, StringComparer.Ordinal))
            .Select(d => ToDto(d, overrides.GetValueOrDefault(d.Key), CanEdit(d, granted)))
            .ToList());
    }

    public async Task<Result<IReadOnlyList<SettingDto>>> UpdateAsync(
        Guid clientId, UpdateSettingsRequest request, IReadOnlyCollection<string>? groups, CancellationToken cancellationToken)
    {
        if (await _clients.GetByIdAsync(clientId, cancellationToken) is null)
        {
            return Error.NotFound();
        }

        var granted = await _permissions.GetPermissionsAsync(_currentUser.Roles, cancellationToken);
        var overrides = (await _settings.GetAllAsync(clientId, cancellationToken)).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var fieldErrors = new Dictionary<string, string[]>();
        var changes = new List<(SettingDefinition Definition, string Json)>();

        foreach (var (key, value) in request.Values)
        {
            var definition = SettingCatalog.Find(key);
            if (definition is null || (groups is not null && !groups.Contains(definition.Group, StringComparer.Ordinal)))
            {
                fieldErrors[key] = ["Unknown setting."];
                continue;
            }

            if (!CanEdit(definition, granted))
            {
                return Error.Forbidden(ErrorCodes.Forbidden, $"You cannot change '{key}'.");
            }

            var (json, error) = SettingCatalog.Normalize(definition, value);
            if (error is not null)
            {
                fieldErrors[key] = [error];
                continue;
            }

            changes.Add((definition, json!));
        }

        if (fieldErrors.Count > 0)
        {
            return Error.Validation("Some settings are invalid.", fieldErrors);
        }

        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        foreach (var (definition, json) in changes)
        {
            var isDefault = SettingCatalog.IsDefault(definition, json);
            overrides.TryGetValue(definition.Key, out var existing);
            before[definition.Key] = existing?.ValueJson ?? "default";
            after[definition.Key] = json;

            if (isDefault)
            {
                if (existing is not null)
                {
                    _settings.Remove(existing); // back to default: store nothing
                }

                continue;
            }

            if (existing is null)
            {
                _settings.Add(ClientSetting.Create(clientId, definition.Key, json));
            }
            else
            {
                existing.SetValue(json);
            }
        }

        if (changes.Count > 0)
        {
            _audit.Record(new AuditEntry("client.settings_updated", nameof(ClientSetting), clientId.ToString(), clientId, OldValues: before, NewValues: after));
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(clientId, groups, cancellationToken);
    }

    public async Task<ClientSettingsSnapshot> GetEffectiveAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var overrides = (await _settings.GetAllAsync(clientId, cancellationToken)).ToDictionary(s => s.Key, s => s.ValueJson, StringComparer.Ordinal);
        var values = SettingCatalog.All.ToDictionary(
            d => d.Key,
            d => overrides.TryGetValue(d.Key, out var json) ? JsonDocument.Parse(json).RootElement.Clone() : SettingCatalog.ToJson(d.Default),
            StringComparer.Ordinal);
        return new ClientSettingsSnapshot(values);
    }

    private bool CanEdit(SettingDefinition definition, IReadOnlySet<string> granted)
    {
        if (_currentUser.IsPlatformUser)
        {
            return granted.Contains(Permissions.Clients.Settings);
        }

        return definition.ManagedBy == SettingManager.Client
            && definition.ClientEditPermission is { } permission
            && granted.Contains(permission);
    }

    private static SettingDto ToDto(SettingDefinition d, ClientSetting? overrideRow, bool editable)
    {
        var current = overrideRow is null ? SettingCatalog.ToJson(d.Default) : JsonDocument.Parse(overrideRow.ValueJson).RootElement.Clone();
        return new SettingDto(d.Key, d.Group, d.Type.ToString(), current, SettingCatalog.ToJson(d.Default), d.Min, d.Max,
            d.ManagedBy.ToString(), editable, overrideRow is not null, d.Description);
    }
}
