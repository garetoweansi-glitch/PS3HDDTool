using System.Buffers.Binary;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;

namespace PS3HddTool.Avalonia.ViewModels;

/// <summary>
/// Helper class to handle recovery mode operations when standard filesystem mount fails.
/// Integrates with MainViewModel to provide fallback recovery scanning and file access.
/// </summary>
public class RecoveryModeHelper
{
    private readonly IDiskSource _diskSource;
    private readonly long _partitionSector;
    private readonly Action<string> _logCallback;

    public Ufs2FileSystemRecovery? RecoveryFilesystem { get; private set; }
    public bool IsRecoveryMode { get; private set; }

    public RecoveryModeHelper(IDiskSource diskSource, long partitionSector, Action<string> logCallback)
    {
        _diskSource = diskSource;
        _partitionSector = partitionSector;
        _logCallback = logCallback;
    }

    /// <summary>
    /// Attempt to mount filesystem normally; if that fails, trigger recovery scan.
    /// </summary>
    public async Task<(bool Success, Ufs2FileSystemRecovery? RecoveryFs, string ErrorMsg)> TryMountFilesystemAsync()
    {
        try
        {
            // Try standard mount first
            var standardFs = new Ufs2FileSystem(_diskSource, _partitionSector);
            if (standardFs.Mount())
            {
                _logCallback("Standard mount succeeded.");
                return (true, null, "");
            }
        }
        catch (Exception ex)
        {
            _logCallback($"Standard mount failed: {ex.Message}");
        }

        _logCallback("Entering recovery mode...");

        // Standard mount failed — scan for superblocks
        var scanner = new Ufs2RecoveryScanner(
            _diskSource,
            _partitionSector,
            msg => _logCallback($"[RECOVERY SCAN] {msg}"));

        List<(long Offset, Ufs2Superblock Superblock)> candidates = 
            await Task.Run(() => scanner.ScanForSuperblocks());

        if (candidates.Count == 0)
        {
            _logCallback("FATAL: No valid UFS2 superblocks found.");
            return (false, null, "No superblocks found in recovery scan");
        }

        _logCallback($"Found {candidates.Count} candidate superblock(s)");

        // Score and rank candidates
        var ranked = new List<(long Offset, Ufs2Superblock Sb, double Score)>();

        foreach (var (offset, sb) in candidates)
        {
            var (isValid, reason, confidence) = scanner.VerifySuperblock(
                sb, _diskSource.TotalSize - _partitionSector * 512);

            ranked.Add((offset, sb, confidence));
            _logCallback(
                $"  Superblock at 0x{offset:X8}: confidence={confidence:P0} ({reason})");
        }

        // Pick the highest confidence one
        var best = ranked.OrderByDescending(x => x.Score).First();
        _logCallback($"Selected superblock at offset 0x{best.Offset:X8} (confidence={best.Score:P0})");

        // Create recovery filesystem
        var recoveryFs = scanner.CreateRecoveryFilesystem(best.Offset, best.Sb);
        RecoveryFilesystem = recoveryFs;
        IsRecoveryMode = true;

        _logCallback("Recovery filesystem initialized.");
        return (true, recoveryFs, "");
    }

    /// <summary>
    /// Build a file tree from the recovery filesystem.
    /// </summary>
    public async Task<List<FileTreeNode>> BuildRecoveryFileTree(Ufs2FileSystemRecovery recFs)
    {
        var rootChildren = new List<FileTreeNode>();

        await Task.Run(() =>
        {
            var (success, rootInode, errorMsg) = recFs.TryReadInode(2);
            if (!success)
            {
                _logCallback($"Failed to read root inode: {errorMsg}");
                return;
            }

            var entries = recFs.TryReadDirectory(rootInode!, out int corruptedCount);
            if (corruptedCount > 0)
            {
                _logCallback($"  WARNING: {corruptedCount} corrupted directory entries skipped");
            }

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var (entrySuccess, entryInode, _) = recFs.TryReadInode(entry.InodeNumber);
                if (!entrySuccess)
                {
                    _logCallback($"  Skipping corrupted entry: {entry.Name}");
                    continue;
                }

                try
                {
                    var node = FileTreeNode.FromInode(entryInode!, entry.Name, "/", 2);
                    rootChildren.Add(node);
                }
                catch
                {
                    _logCallback($"  Failed to create tree node for {entry.Name}");
                }
            }
        });

        return rootChildren;
    }

    /// <summary>
    /// Attempt to extract a file from the recovery filesystem, reporting partial recovery.
    /// </summary>
    public async Task<(bool PartialSuccess, long BytesRecovered, string ErrorMsg)> TryExtractFileAsync(
        Ufs2FileSystemRecovery recFs,
        long inodeNumber,
        Stream outputStream,
        Action<long>? progressCallback = null)
    {
        return await Task.Run(() =>
        {
            var (success, inode, errorMsg) = recFs.TryReadInode(inodeNumber);
            if (!success)
                return (false, 0L, errorMsg);

            byte[] fileData = recFs.TryReadInodeData(inode!, out int bytesRead);

            if (bytesRead > 0)
            {
                outputStream.Write(fileData, 0, bytesRead);
                progressCallback?.Invoke(bytesRead);

                // Check if we recovered all data
                bool isComplete = bytesRead >= inode!.Size;
                if (!isComplete)
                {
                    _logCallback($"  Partial recovery: {bytesRead}/{inode.Size} bytes recovered");
                }

                return (true, bytesRead, isComplete ? "" : "Partial recovery");
            }

            return (false, 0L, "Could not read any data");
        });
    }

    /// <summary>
    /// Deep scan to assess filesystem readability across many inodes.
    /// Reports statistics on corrupted vs readable files.
    /// </summary>
    public async Task<(int ReadableFiles, int CorruptedFiles, long RecoverableBytes)> PerformDeepScanAsync(
        Ufs2FileSystemRecovery recFs)
    {
        int readableCount = 0;
        int corruptedCount = 0;
        long recoverableBytes = 0;

        await Task.Run(() =>
        {
            var (rootSuccess, rootInode, _) = recFs.TryReadInode(2);
            if (!rootSuccess)
                return;

            var entries = recFs.TryReadDirectory(rootInode!, out _);
            int processed = 0;

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..")
                    continue;

                var (entrySuccess, entryInode, _) = recFs.TryReadInode(entry.InodeNumber);
                if (!entrySuccess)
                {
                    corruptedCount++;
                    continue;
                }

                if (entryInode!.FileType == Ufs2FileType.RegularFile)
                {
                    byte[] data = recFs.TryReadInodeData(entryInode, out int bytesRead);
                    if (bytesRead > 0)
                    {
                        readableCount++;
                        recoverableBytes += bytesRead;
                    }
                    else
                    {
                        corruptedCount++;
                    }
                }

                processed++;
                if (processed % 100 == 0)
                    _logCallback($"Scanned {processed} entries...");
            }
        });

        return (readableCount, corruptedCount, recoverableBytes);
    }
}
