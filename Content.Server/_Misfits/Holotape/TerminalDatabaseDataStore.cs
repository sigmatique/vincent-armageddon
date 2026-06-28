using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Utility;

// #Misfits Add - Persistent JSON data store for faction-shared terminal databases.
// Mirrors the TerminalNotesDataStore pattern: single IoC singleton, JSON on disk
// at /terminal_databases.json via IResourceManager.UserData. Survives round AND
// server restart. Schema is shaped to accommodate Stage 2 (review queue) and
// Stage 3 (pinned docs / sort) without migration.

namespace Content.Server._Misfits.Holotape;

/// <summary>
/// IoC singleton that loads, mutates, and persists faction-shared terminal databases.
/// Keyed by databaseId (the prototype id from MisfitsTerminalDatabasePrototype).
/// </summary>
public sealed class TerminalDatabaseDataStore
{
    [Dependency] private readonly IResourceManager _resourceManager = default!;

    private static readonly ResPath StoragePath = new("/terminal_databases.json");

    // Defer sawmill init to Initialize() — IoC isn't fully wired during field init.
    private ISawmill _sawmill = default!;

    /// <summary>
    /// In-memory store: databaseId → list of top-level folders.
    /// </summary>
    private Dictionary<string, List<FolderDto>> _store = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public void Initialize()
    {
        _sawmill = Logger.GetSawmill("terminal.database");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!_resourceManager.UserData.Exists(StoragePath))
            {
                _sawmill.Info("No terminal_databases.json found; starting fresh.");
                return;
            }

            var json = _resourceManager.UserData.ReadAllText(StoragePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<FolderDto>>>(json, JsonOptions);
            if (loaded != null)
            {
                _store = loaded;
                _sawmill.Info($"Loaded terminal databases for {_store.Count} faction(s).");
            }
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to load terminal databases: {ex}");
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_store, JsonOptions);
            _resourceManager.UserData.WriteAllText(StoragePath, json);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to save terminal databases: {ex}");
        }
    }

    // ── Read accessors ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the raw folder list for a database (creating an empty entry if none exists).
    /// Caller should not mutate the returned list directly — use the API methods below.
    /// </summary>
    public List<FolderDto> GetFolders(string databaseId)
    {
        if (!_store.TryGetValue(databaseId, out var folders))
        {
            folders = new List<FolderDto>();
            _store[databaseId] = folders;
        }
        return folders;
    }

    /// <summary>
    /// Resolves a document by id across all folders/subfolders of a database.
    /// Returns null if the document does not exist.
    /// </summary>
    public DocumentDto? FindDocument(string databaseId, Guid documentId)
    {
        if (!_store.TryGetValue(databaseId, out var folders))
            return null;

        foreach (var folder in folders)
        {
            foreach (var doc in folder.Documents)
                if (doc.DocumentId == documentId)
                    return doc;
            foreach (var sub in folder.Subfolders)
                foreach (var doc in sub.Documents)
                    if (doc.DocumentId == documentId)
                        return doc;
        }
        return null;
    }

    // ── Write API ────────────────────────────────────────────────────────────

    public FolderDto? AddFolder(string databaseId, string name, bool isAdmin, NetUserId? userId, string charName)
    {
        var folders = GetFolders(databaseId);
        var folder = new FolderDto
        {
            FolderId = Guid.NewGuid(),
            Name = name,
            Deleted = false,
            IsAdmin = isAdmin, // #Misfits Add - Admin-protected root folders.
            CreatedByUserIdGuid = userId?.UserId,
            CreatedByCharName = charName,
            CreatedAt = DateTime.UtcNow,
            Subfolders = new List<SubfolderDto>(),
            Documents = new List<DocumentDto>(),
        };
        folders.Add(folder);
        Save();
        return folder;
    }

    public SubfolderDto? AddSubfolder(string databaseId, Guid parentFolderId, string name, NetUserId? userId, string charName)
    {
        var folders = GetFolders(databaseId);
        var parent = folders.Find(f => f.FolderId == parentFolderId && !f.Deleted);
        if (parent == null)
            return null;

        var sub = new SubfolderDto
        {
            SubfolderId = Guid.NewGuid(),
            Name = name,
            Deleted = false,
            CreatedByUserIdGuid = userId?.UserId,
            CreatedByCharName = charName,
            CreatedAt = DateTime.UtcNow,
            Documents = new List<DocumentDto>(),
        };
        parent.Subfolders.Add(sub);
        Save();
        return sub;
    }

    public DocumentDto? AddDocument(
        string databaseId,
        Guid folderId,
        Guid? subfolderId,
        string title,
        string body,
        bool isAdmin,
        NetUserId? userId,
        string charName)
    {
        var folders = GetFolders(databaseId);
        var folder = folders.Find(f => f.FolderId == folderId && !f.Deleted);
        if (folder == null)
            return null;

        List<DocumentDto> targetList;
        if (subfolderId.HasValue)
        {
            var sub = folder.Subfolders.Find(s => s.SubfolderId == subfolderId.Value && !s.Deleted);
            if (sub == null)
                return null;
            targetList = sub.Documents;
        }
        else
        {
            targetList = folder.Documents;
        }

        var now = DateTime.UtcNow;
        var doc = new DocumentDto
        {
            DocumentId = Guid.NewGuid(),
            Title = title,
            Deleted = false,
            IsAdmin = isAdmin, // #Misfits Add - per-document Admin protection.
            CreatedByUserIdGuid = userId?.UserId,
            CreatedByCharName = charName,
            CreatedAt = now,
            Revisions = new List<RevisionDto>
            {
                new()
                {
                    RevisionNumber = 1,
                    Body = body,
                    AuthorUserIdGuid = userId?.UserId,
                    AuthorCharName = charName,
                    Timestamp = now,
                },
            },
        };
        targetList.Add(doc);
        Save();
        return doc;
    }

    public bool AppendRevision(string databaseId, Guid documentId, string body, NetUserId? userId, string charName)
    {
        var doc = FindDocument(databaseId, documentId);
        if (doc == null || doc.Deleted)
            return false;

        var nextNumber = doc.Revisions.Count == 0 ? 1 : doc.Revisions[^1].RevisionNumber + 1;
        doc.Revisions.Add(new RevisionDto
        {
            RevisionNumber = nextNumber,
            Body = body,
            AuthorUserIdGuid = userId?.UserId,
            AuthorCharName = charName,
            Timestamp = DateTime.UtcNow,
        });
        Save();
        return true;
    }

    public bool RollbackDocument(string databaseId, Guid documentId, int targetRevisionNumber, NetUserId? userId, string charName)
    {
        var doc = FindDocument(databaseId, documentId);
        if (doc == null || doc.Deleted)
            return false;

        var targetIndex = doc.Revisions.FindIndex(r => r.RevisionNumber == targetRevisionNumber);
        if (targetIndex < 0)
            return false;

        // #Misfits Change - True rollback: drop every revision after the target so the
        // chosen revision becomes the current body. Previous behavior appended a NEW
        // revision per click, which spammed an unbounded row of [ROLLBACK rN] buttons
        // since the client renders one button per non-current revision. Truncating keeps
        // the history clean and prevents the UI from filling up indefinitely.
        if (targetIndex < doc.Revisions.Count - 1)
        {
            doc.Revisions.RemoveRange(targetIndex + 1, doc.Revisions.Count - targetIndex - 1);
        }
        // Stamp the current (now-target) revision's authorship so others can see who rolled back.
        var current = doc.Revisions[targetIndex];
        current.AuthorCharName = $"{charName} (rollback to r{targetRevisionNumber})";
        current.AuthorUserIdGuid = userId?.UserId;
        current.Timestamp = DateTime.UtcNow;

        Save();
        return true;
    }

    public bool SoftDeleteFolder(string databaseId, Guid folderId, Guid? subfolderId)
    {
        var folders = GetFolders(databaseId);
        var folder = folders.Find(f => f.FolderId == folderId);
        if (folder == null)
            return false;

        if (subfolderId.HasValue)
        {
            var sub = folder.Subfolders.Find(s => s.SubfolderId == subfolderId.Value);
            if (sub == null)
                return false;
            sub.Deleted = true;
        }
        else
        {
            folder.Deleted = true;
        }
        Save();
        return true;
    }

    public bool SoftDeleteDocument(string databaseId, Guid documentId)
    {
        var doc = FindDocument(databaseId, documentId);
        if (doc == null)
            return false;
        doc.Deleted = true;
        Save();
        return true;
    }

    public bool RestoreFolder(string databaseId, Guid folderId, Guid? subfolderId)
    {
        var folders = GetFolders(databaseId);
        var folder = folders.Find(f => f.FolderId == folderId);
        if (folder == null)
            return false;

        if (subfolderId.HasValue)
        {
            var sub = folder.Subfolders.Find(s => s.SubfolderId == subfolderId.Value);
            if (sub == null)
                return false;
            sub.Deleted = false;
        }
        else
        {
            folder.Deleted = false;
        }
        Save();
        return true;
    }

    public bool RestoreDocument(string databaseId, Guid documentId)
    {
        var doc = FindDocument(databaseId, documentId);
        if (doc == null)
            return false;
        doc.Deleted = false;
        Save();
        return true;
    }

    // #Misfits Add - Permanent (hard) delete: actually remove entries from the store.
    // Unlike soft-delete, these cannot be restored.

    /// <summary>
    /// Permanently removes a top-level folder (and all contents) from the database.
    /// </summary>
    public bool HardDeleteFolder(string databaseId, Guid folderId)
    {
        var folders = GetFolders(databaseId);
        var idx = folders.FindIndex(f => f.FolderId == folderId);
        if (idx < 0)
            return false;
        folders.RemoveAt(idx);
        Save();
        return true;
    }

    /// <summary>
    /// Permanently removes a subfolder (and all contents) from its parent folder.
    /// </summary>
    public bool HardDeleteSubfolder(string databaseId, Guid folderId, Guid subfolderId)
    {
        var folders = GetFolders(databaseId);
        var folder = folders.Find(f => f.FolderId == folderId);
        if (folder == null)
            return false;
        var idx = folder.Subfolders.FindIndex(s => s.SubfolderId == subfolderId);
        if (idx < 0)
            return false;
        folder.Subfolders.RemoveAt(idx);
        Save();
        return true;
    }

    /// <summary>
    /// Permanently removes a single document from wherever it lives
    /// (folder root or subfolder). Returns true if found and removed.
    /// </summary>
    public bool HardDeleteDocument(string databaseId, Guid documentId)
    {
        var folders = GetFolders(databaseId);
        foreach (var folder in folders)
        {
            var idx = folder.Documents.FindIndex(d => d.DocumentId == documentId);
            if (idx >= 0)
            {
                folder.Documents.RemoveAt(idx);
                Save();
                return true;
            }
            foreach (var sub in folder.Subfolders)
            {
                var sidx = sub.Documents.FindIndex(d => d.DocumentId == documentId);
                if (sidx >= 0)
                {
                    sub.Documents.RemoveAt(sidx);
                    Save();
                    return true;
                }
            }
        }
        return false;
    }

    // ── DTO classes (System.Text.Json compatible) ────────────────────────────

    /// <summary>JSON-friendly folder DTO. NetUserId stored as Guid? since STJ can't handle it directly.</summary>
    public sealed class FolderDto
    {
        public Guid FolderId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Deleted { get; set; }
        // #Misfits Add - If true, only Admin-tier (job-gated) actors can delete this
        // folder or its contents. Defaults to false so legacy JSON deserializes cleanly.
        // JSON property kept as "IsElder" so any pre-rename saved files still load.
        [System.Text.Json.Serialization.JsonPropertyName("IsElder")]
        public bool IsAdmin { get; set; }
        public Guid? CreatedByUserIdGuid { get; set; }
        public string CreatedByCharName { get; set; } = "Unknown";
        public DateTime CreatedAt { get; set; }
        public List<SubfolderDto> Subfolders { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();
    }

    public sealed class SubfolderDto
    {
        public Guid SubfolderId { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Deleted { get; set; }
        public Guid? CreatedByUserIdGuid { get; set; }
        public string CreatedByCharName { get; set; } = "Unknown";
        public DateTime CreatedAt { get; set; }
        public List<DocumentDto> Documents { get; set; } = new();
    }

    public sealed class DocumentDto
    {
        public Guid DocumentId { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool Deleted { get; set; }
        // #Misfits Add - True if this document is independently Admin-protected.
        // Either this OR the parent folder's IsAdmin forces Admin tier for edit/delete.
        // Defaults to false; old JSON deserializes cleanly.
        public bool IsAdmin { get; set; }
        public Guid? CreatedByUserIdGuid { get; set; }
        public string CreatedByCharName { get; set; } = "Unknown";
        public DateTime CreatedAt { get; set; }
        public List<RevisionDto> Revisions { get; set; } = new();
    }

    public sealed class RevisionDto
    {
        public int RevisionNumber { get; set; }
        public string Body { get; set; } = string.Empty;
        public Guid? AuthorUserIdGuid { get; set; }
        public string AuthorCharName { get; set; } = "Unknown";
        public DateTime Timestamp { get; set; }
    }
}
