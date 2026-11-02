using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using WWDS.ModCore.Config;
using WWDS.ModCore.Features;

namespace WWDS.Game.Wwds.Mods;

/// <summary>
/// Save backups and a cloud mirror (the games have no Steam Cloud). At every start the saves (*.bin) are
/// copied to BepInEx/wwds/backups when they changed; the newest ones are kept. A backup can be restored
/// from the mod menu: it is put back at the next start, before the game reads its saves. With a mirror
/// folder (e.g. in OneDrive or Dropbox) every change is copied there, and at start a newer state from
/// the mirror (another PC) is taken over, after a backup of the local one.
/// </summary>
public sealed class SaveBackupFeature : FeatureBase
{
    public const string FeatureId = "SaveBackup";
    private const string PendingFile = "restore-pending.txt";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    private IConfigValue<bool> _enabled = null!;
    private IConfigValue<int> _keep = null!;
    private IConfigValue<string> _mirror = null!;
    private string _saves = string.Empty;
    private string _root = string.Empty;
    private DateTime _lastExport;
    private float _nextCheck;

    public override string Id => FeatureId;

    public override string DisplayName => "Save backups";

    public override string Description => "Backs up the saves at every start, restores a backup, mirrors saves to a cloud folder.";

    protected override string LogCategoryName => "Mods";

    public string BackupFolder => _root;

    public IConfigValue<string> MirrorFolder => _mirror;

    /// <summary>Backup chosen for the next start, empty if none.</summary>
    public string PendingRestore => File.Exists(Path.Combine(_root, PendingFile)) ? File.ReadAllText(Path.Combine(_root, PendingFile)).Trim() : string.Empty;

    protected override void OnStart()
    {
        var c = Context.Config;
        _enabled = c.Bind("Saves", "BackupAtStart", true, "Copy the saves to BepInEx/wwds/backups at every start when they changed.");
        _keep = c.Bind("Saves", "KeepBackups", 10, "Number of backups kept (oldest are deleted).");
        _mirror = c.Bind("Saves", "MirrorFolder", "", "Folder to mirror the saves to, e.g. in OneDrive (empty = off). A newer state there is taken over at start.");
        var dir = Context.Host?.DataDirectory;
        if (dir == null) return;
        _root = Path.Combine(dir, "backups");
        _saves = Application.persistentDataPath;
        Context.Provide(this);
        try
        {
            Directory.CreateDirectory(_root);
            ApplyPendingRestore();
            ImportFromMirror();
            if (_enabled.Value) Backup("start", onlyIfChanged: true);
            _lastExport = LatestSaveTime();
        }
        catch (Exception ex)
        {
            Log.Warn("Save backup at start failed: " + ex.Message);
        }
    }

    protected override void OnStop() => ExportToMirror();

    public override void Tick()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 30f;
        if (LatestSaveTime() > _lastExport) ExportToMirror();
    }

    /// <summary>Backups, newest first: folder name and when it was made.</summary>
    public IList<string> Backups() => Directory.Exists(_root)
        ? Directory.GetDirectories(_root).Select(Path.GetFileName).OrderByDescending(n => n, StringComparer.Ordinal).ToList()
        : new List<string>();

    /// <summary>Makes a backup now; returns its name (empty if nothing to save or unchanged).</summary>
    public string Backup(string reason, bool onlyIfChanged)
    {
        var files = SaveFiles(_saves);
        if (files.Length == 0) return string.Empty;
        var latest = Backups().FirstOrDefault();
        if (onlyIfChanged && latest != null && Same(_saves, Path.Combine(_root, latest))) return string.Empty;
        var name = DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture) + "_" + reason;
        var target = Path.Combine(_root, name);
        Directory.CreateDirectory(target);
        foreach (var f in files) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
        Log.Info("Saves backed up: " + name + " (" + files.Length + " files)");
        Prune();
        return name;
    }

    /// <summary>Marks a backup to be put back at the next start (the running game would overwrite it).</summary>
    public void RestoreAtNextStart(string backup) => File.WriteAllText(Path.Combine(_root, PendingFile), backup);

    public void CancelRestore()
    {
        var path = Path.Combine(_root, PendingFile);
        if (File.Exists(path)) File.Delete(path);
    }

    private void ApplyPendingRestore()
    {
        var name = PendingRestore;
        if (name.Length == 0) return;
        CancelRestore();
        var source = Path.Combine(_root, name);
        if (!Directory.Exists(source))
        {
            Log.Warn("Backup " + name + " to restore no longer exists");
            return;
        }

        Backup("before-restore", onlyIfChanged: false);
        CopyAll(source, _saves);
        Log.Info("Saves restored from backup " + name);
    }

    private void ImportFromMirror()
    {
        var mirror = MirrorPath();
        if (mirror == null || SaveFiles(mirror).Length == 0) return;
        var remote = SaveFiles(mirror).Max(f => File.GetLastWriteTimeUtc(f));
        if (remote <= LatestSaveTime().AddSeconds(2) || Same(mirror, _saves)) return;
        Backup("before-mirror", onlyIfChanged: false);
        CopyAll(mirror, _saves);
        Log.Info("Newer saves taken over from the mirror folder " + mirror);
    }

    private void ExportToMirror()
    {
        var mirror = MirrorPath();
        _lastExport = LatestSaveTime();
        if (mirror == null) return;
        try
        {
            Directory.CreateDirectory(mirror);
            CopyAll(_saves, mirror);
        }
        catch (Exception ex)
        {
            Log.Warn("Saves could not be mirrored: " + ex.Message);
        }
    }

    /// <summary>Mirror folder for this game (each game has its own subfolder).</summary>
    private string? MirrorPath()
    {
        var folder = _mirror.Value.Trim();
        if (folder.Length == 0) return null;
        return Path.Combine(folder, Path.GetFileName(_saves.TrimEnd('/', '\\')));
    }

    private DateTime LatestSaveTime()
    {
        var files = SaveFiles(_saves);
        return files.Length == 0 ? DateTime.MinValue : files.Max(f => File.GetLastWriteTimeUtc(f));
    }

    private static string[] SaveFiles(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin") : new string[0];

    private static void CopyAll(string from, string to)
    {
        foreach (var f in SaveFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(f));
            File.Copy(f, target, true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(f));
        }
    }

    /// <summary>Same save files with the same bytes.</summary>
    private static bool Same(string a, string b)
    {
        var fa = SaveFiles(a).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var fb = SaveFiles(b).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (!fa.SequenceEqual(fb)) return false;
        return fa.All(n => File.ReadAllBytes(Path.Combine(a, n)).SequenceEqual(File.ReadAllBytes(Path.Combine(b, n))));
    }

    private void Prune()
    {
        foreach (var old in Backups().Skip(Math.Max(1, _keep.Value)))
        {
            try
            {
                Directory.Delete(Path.Combine(_root, old), true);
            }
            catch (IOException ex)
            {
                Log.Warn("Old backup " + old + " could not be removed: " + ex.Message);
            }
        }
    }
}
