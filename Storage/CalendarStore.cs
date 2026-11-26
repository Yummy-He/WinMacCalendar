using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace WinMacCalendar;

public sealed record Resource(string Href, string Ics, string Etag = "", string Pending = "");

public static class StorageLocation
{
    private const string RegKey = @"Software\WinMacCalendar";
    private const string RegValue = "DataFolder";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMacCalendar");

    public static string GetCurrentFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, false);
            if (key?.GetValue(RegValue) is string saved && !string.IsNullOrWhiteSpace(saved))
                return Path.GetFullPath(saved.Trim());
        }
        catch { }
        return DefaultFolder;
    }

    public static bool IsDefault(string folder) =>
        string.Equals(Normalize(folder), Normalize(DefaultFolder), StringComparison.OrdinalIgnoreCase);

    public static void SetFolder(string? folder)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegKey);
        if (string.IsNullOrWhiteSpace(folder) || IsDefault(folder))
            key.DeleteValue(RegValue, throwOnMissingValue: false);
        else
            key.SetValue(RegValue, Normalize(folder));
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public sealed class CalendarStore : IDisposable
{
    public const string Local = "local";
    private SqliteConnection db;
    public string DbPath { get; private set; }
    public string FolderPath => DbPath == ":memory:" ? StorageLocation.DefaultFolder : (Path.GetDirectoryName(DbPath) ?? StorageLocation.DefaultFolder);
    public long Revision { get; private set; }

    public CalendarStore(string path)
    {
        DbPath = path;
        db = OpenConnection(path);
    }

    private static SqliteConnection OpenConnection(string path)
    {
        if (path != ":memory:")
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var schema = conn.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS resources (scope TEXT NOT NULL, href TEXT NOT NULL, ics TEXT NOT NULL,
                etag TEXT NOT NULL, pending TEXT NOT NULL, PRIMARY KEY(scope,href));
            """;
        schema.ExecuteNonQuery();
        return conn;
    }

    public void MigrateToFolder(string targetFolder, bool useExistingTargetIfPresent)
    {
        if (DbPath == ":memory:") throw new InvalidOperationException("内存数据库不支持迁移目录。");
        string normalizedTarget = StorageLocation.Normalize(targetFolder);
        string currentFolder = StorageLocation.Normalize(FolderPath);
        if (string.Equals(normalizedTarget, currentFolder, StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(normalizedTarget);
        string oldPath = DbPath;
        string newPath = Path.Combine(normalizedTarget, "calendar.db");
        bool targetExists = File.Exists(newPath);

        // Flush WAL if any and close connection cleanly before moving file.
        try
        {
            using var ck = db.CreateCommand();
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
        catch { }

        db.Close();
        db.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (!targetExists || !useExistingTargetIfPresent)
            {
                if (File.Exists(oldPath))
                    File.Copy(oldPath, newPath, overwrite: true);
            }

            // Open and verify the new database before touching the old file.
            db = OpenConnection(newPath);
            DbPath = newPath;
            Revision++;

            // Clean up old database file (best effort) after new DB is verified open.
            if (File.Exists(oldPath))
            {
                try
                {
                    File.Delete(oldPath);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                        if (File.Exists(oldPath + suffix)) File.Delete(oldPath + suffix);
                    if (Directory.Exists(currentFolder) && !Directory.EnumerateFileSystemEntries(currentFolder).Any())
                        Directory.Delete(currentFolder);
                }
                catch { }
            }
        }
        catch
        {
            // Roll back to old database cleanly so the app never loses state.
            db = OpenConnection(oldPath);
            DbPath = oldPath;
            throw;
        }
    }

    private SqliteCommand Command(string sql, params (string Key, string Value)[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Key, arg.Value);
        return command;
    }

    public string Get(string key, string fallback = "")
    {
        using var cmd = Command("SELECT value FROM settings WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    public void Set(string key, string value)
    {
        using var cmd = Command("INSERT OR REPLACE INTO settings VALUES ($k,$v)", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    public List<Resource> List(string scope)
    {
        using var cmd = Command("SELECT href,ics,etag,pending FROM resources WHERE scope=$s", ("$s", scope));
        using var r = cmd.ExecuteReader();
        var list = new List<Resource>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public void Save(string scope, Resource item)
    {
        using var cmd = Command("INSERT OR REPLACE INTO resources VALUES ($s,$h,$i,$e,$p)",
            ("$s", scope), ("$h", item.Href), ("$i", item.Ics), ("$e", item.Etag), ("$p", item.Pending));
        cmd.ExecuteNonQuery();
        Revision++;
    }

    public void Remove(string scope, string href)
    {
        using var cmd = Command("DELETE FROM resources WHERE scope=$s AND href=$h", ("$s", scope), ("$h", href));
        cmd.ExecuteNonQuery();
        Revision++;
    }

    public void Apply(string scope, IEnumerable<Resource> changed, IEnumerable<string> removed, string token)
    {
        using var transaction = db.BeginTransaction();
        foreach (var item in changed) Save(scope, item);
        foreach (var href in removed) Remove(scope, href);
        Set("token:" + scope, token);
        transaction.Commit();
    }

    public void Dispose()
    {
        db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
