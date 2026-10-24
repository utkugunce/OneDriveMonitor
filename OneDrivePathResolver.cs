using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace OneDriveMonitor
{
    public class FolderRecord
    {
        public string ParentId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public static class OneDrivePathResolver
    {
        private static readonly Dictionary<string, FolderRecord> _folderCache = new(StringComparer.OrdinalIgnoreCase);
        private static string _oneDriveRoot = "";
        private static DateTime _lastCacheRefresh = DateTime.MinValue;

        public static string GetOneDriveRoot()
        {
            if (!string.IsNullOrEmpty(_oneDriveRoot) && Directory.Exists(_oneDriveRoot))
                return _oneDriveRoot;

            try
            {
                // Registry'den kontrol et
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
                if (key?.GetValue("UserFolder") is string path && Directory.Exists(path))
                {
                    _oneDriveRoot = path;
                    return _oneDriveRoot;
                }
            }
            catch { }

            // Varsayılan ortam değişkeni
            string defaultPath = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\OneDrive");
            if (Directory.Exists(defaultPath))
            {
                _oneDriveRoot = defaultPath;
                return _oneDriveRoot;
            }

            return defaultPath;
        }

        public static void RefreshFolderCache(SqliteConnection conn)
        {
            if ((DateTime.UtcNow - _lastCacheRefresh).TotalSeconds < 30 && _folderCache.Count > 0)
                return;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT resourceID, parentResourceID, folderName FROM od_ClientFolder_Records";
                using var reader = cmd.ExecuteReader();

                _folderCache.Clear();
                while (reader.Read())
                {
                    string resId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string parentId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    string name = reader.IsDBNull(2) ? "" : reader.GetString(2);

                    if (!string.IsNullOrEmpty(resId))
                    {
                        _folderCache[resId] = new FolderRecord { ParentId = parentId, Name = name };
                    }
                }
                _lastCacheRefresh = DateTime.UtcNow;
            }
            catch { }
        }

        public static string ResolveFullPath(string parentResourceId, string fileName)
        {
            string root = GetOneDriveRoot();
            var parts = new List<string> { fileName };

            string currentId = parentResourceId;
            int depth = 0;
            while (!string.IsNullOrEmpty(currentId) && _folderCache.TryGetValue(currentId, out var folder) && depth < 30)
            {
                if (!string.IsNullOrEmpty(folder.Name))
                {
                    parts.Add(folder.Name);
                }
                currentId = folder.ParentId;
                depth++;
            }

            parts.Reverse();
            try
            {
                return Path.Combine(root, Path.Combine(parts.ToArray()));
            }
            catch
            {
                return Path.Combine(root, fileName);
            }
        }

        private static readonly char[] _invalidFileChars = new[] { '"', '*', ':', '<', '>', '?', '/', '\\', '|' };

        public static bool HasInvalidName(string fileName, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            if (fileName.Any(c => _invalidFileChars.Contains(c)))
            {
                reason = "Geçersiz karakterler içeriyor: \" * : < > ? / \\ |";
                return true;
            }

            if (fileName.EndsWith(" ") || fileName.EndsWith("."))
            {
                reason = "Dosya adı boşluk veya nokta ile bitemez.";
                return true;
            }

            return false;
        }

        public static bool IsPathTooLong(string fullPath)
        {
            return fullPath != null && fullPath.Length >= 260;
        }
    }
}
