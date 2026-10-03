using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PolterSense.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PolterSense.Services;

/// <summary>Single scene authority for API writes and externally edited YAML. Never loads Companion storage.</summary>
public sealed class ConfigLoader : BackgroundService
{
    private sealed record State(ConfigSnapshot Snapshot, string Yaml, string? AcceptedDiskHash, string? Error);
    private readonly string _path;
    private readonly string _backupPath;
    private readonly ILogger<ConfigLoader> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).WithDuplicateKeyChecking().WithMaximumRecursion(32).Build();
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance).ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build();
    private State _state;
    private string? _observedHash;

    public ConfigLoader(string directory, ILogger<ConfigLoader> logger)
    {
        _logger = logger;
        _path = Path.Combine(directory, "config.yaml");
        _backupPath = Path.Combine(directory, "config.last-valid.yaml");
        var empty = Snapshot(new Config());
        _state = new State(empty, _serializer.Serialize(empty.Config), null, null);
        Directory.CreateDirectory(directory);
        try
        {
            if (!File.Exists(_path))
            {
                // CreateNew preserves a file created concurrently with startup.
                using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.UTF8.GetBytes(_state.Yaml);
                stream.Write(bytes); stream.Flush(true);
            }
            Reload();
        }
        catch (Exception ex) when (IsSafeFailure(ex)) { SetError("Scene configuration could not be loaded; using the last valid scene."); }
        if (_state.Error != null)
        {
            try
            {
                var yaml = ReadBounded(_backupPath);
                var snapshot = Parse(yaml);
                _state = _state with { Snapshot = snapshot, Yaml = yaml };
            }
            catch (Exception ex) when (IsSafeFailure(ex)) { /* Empty scene remains available if no valid backup exists. */ }
        }
    }

    public ConfigSnapshot Current => Clone(Volatile.Read(ref _state).Snapshot);
    public string? Error => Volatile.Read(ref _state).Error;
    public string GetYaml() => Volatile.Read(ref _state).Yaml;

    public ConfigView GetView()
    {
        var state = Volatile.Read(ref _state);
        var snapshot = Clone(state.Snapshot);
        return new ConfigView(snapshot.Revision, snapshot.Config, state.Yaml, state.Error);
    }

    public static ConfigSnapshot Snapshot(Config config)
    {
        ConfigValidation.Validate(config);
        var json = JsonSerializer.SerializeToUtf8Bytes(config);
        if (json.Length > ConfigValidation.MaxBytes) throw new ConfigValidationException("Scene configuration exceeds the size limit.");
        return new ConfigSnapshot(Hash(json), JsonSerializer.Deserialize<Config>(json)!);
    }

    public Task<ConfigSnapshot> UpdateAsync(Config config, string expectedRevision, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot(config);
        return UpdateYamlAsync(_serializer.Serialize(snapshot.Config), expectedRevision, cancellationToken);
    }

    public async Task<ConfigSnapshot> UpdateYamlAsync(string yaml, string expectedRevision, CancellationToken cancellationToken = default)
    {
        var snapshot = Parse(yaml);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Volatile.Read(ref _state);
            if (string.IsNullOrEmpty(expectedRevision) || expectedRevision != state.Snapshot.Revision || state.Error != null || ReadDiskHash() != state.AcceptedDiskHash)
                throw new ConfigConflictException();
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteFile(temporary, yaml);
                // Recheck after preparing the replacement to catch external edits during serialization/write.
                if (ReadDiskHash() != state.AcceptedDiskHash) throw new ConfigConflictException();
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var diskHash = Hash(Encoding.UTF8.GetBytes(yaml));
            _observedHash = diskHash;
            Volatile.Write(ref _state, new State(snapshot, yaml, diskHash, null));
            SaveBackup(snapshot);
            return Clone(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigValidationException("Scene configuration could not be saved. Check storage access and reload.");
        }
        finally { _gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _gate.WaitAsync(stoppingToken);
                try { Reload(); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void Reload()
    {
        try
        {
            var yaml = ReadBounded(_path);
            var hash = Hash(Encoding.UTF8.GetBytes(yaml));
            if (hash == _observedHash && _state.Error == null) return;
            _observedHash = hash;
            var snapshot = Parse(yaml);
            Volatile.Write(ref _state, new State(snapshot, yaml, hash, null));
            SaveBackup(snapshot);
        }
        catch (Exception ex) when (IsSafeFailure(ex))
        {
            SetError("Scene configuration is invalid or unreadable; the last valid scene remains active.");
        }
    }

    private ConfigSnapshot Parse(string yaml)
    {
        if (yaml == null || Encoding.UTF8.GetByteCount(yaml) > ConfigValidation.MaxBytes)
            throw new ConfigValidationException("Scene configuration exceeds the size limit.");
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var events = 0;
            while (parser.MoveNext())
            {
                if (++events > 50000 || parser.Current is AnchorAlias)
                    throw new ConfigValidationException("Scene YAML aliases and oversized documents are not supported.");
            }
            var config = _deserializer.Deserialize<Config>(yaml);
            if (config == null) throw new ConfigValidationException("A scene document is required.");
            return Snapshot(config);
        }
        catch (YamlException) { throw new ConfigValidationException("Scene YAML is malformed or contains unsupported fields or values."); }
        catch (ArgumentException) { throw new ConfigValidationException("Scene YAML contains invalid values."); }
    }

    private void SaveBackup(ConfigSnapshot snapshot)
    {
        // Serialize only the accepted typed scene, never unvalidated input or transport credentials.
        var temp = _backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteFile(temp, _serializer.Serialize(snapshot.Config)); File.Move(temp, _backupPath, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning("PolterSense could not persist its last valid scene backup."); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    private static void WriteFile(string path, string yaml)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(yaml);
        stream.Write(bytes); stream.Flush(true);
    }
    private string? ReadDiskHash()
    {
        try { return Hash(Encoding.UTF8.GetBytes(ReadBounded(_path))); }
        catch (Exception ex) when (IsSafeFailure(ex)) { return null; }
    }
    private static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > ConfigValidation.MaxBytes) throw new ConfigValidationException("Scene configuration exceeds the size limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096);
        var buffer = new char[ConfigValidation.MaxBytes + 1];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length > ConfigValidation.MaxBytes) throw new ConfigValidationException("Scene configuration exceeds the size limit.");
        return new string(buffer, 0, length);
    }
    private void SetError(string message)
    {
        if (_state.Error != message) _logger.LogWarning("PolterSense scene reload failed; the last valid configuration remains active.");
        Volatile.Write(ref _state, _state with { Error = message });
    }
    private static bool IsSafeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ConfigValidationException or YamlException;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ConfigSnapshot Clone(ConfigSnapshot snapshot) => new(snapshot.Revision, JsonSerializer.Deserialize<Config>(JsonSerializer.SerializeToUtf8Bytes(snapshot.Config))!);
}
