using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;

namespace PS3HddTool.Core.Recovery;

/// <summary>
/// Example integration for recovery mode into the main application.
/// Shows how to use Ufs2RecoveryScanner and Ufs2FileSystemRecovery.
/// </summary>
public class RecoveryModeHelper
{
    private readonly IDiskSource _diskSource;
    private readonly Action<string>? _progressCallback;

    public RecoveryModeHelper(IDiskSource diskSource, Action<string>? progressCallback = null)
    {
        _diskSource = diskSource;
        _progressCallback = progressCallback;
    }

    /// <summary>
    /// Attempt standard mount first, then fall back to recovery scanning.
    /// </summary>
    public async Task<(bool Success, Ufs2FileSystem? NormalFilesystem, Ufs2FileSystemRecovery? RecoveryFilesystem, string Message)>
        TryMountFilesystemAsync(long partitionStartSector)
    {
        // Try standard mount first
        var standardFs = new Ufs2FileSystem(_diskSource, partitionStartSector);
        if (standardFs.Mount())
        {
            return (true, standardFs, null, "Successfully mounted filesystem at standard location");
        }

        _progressCallback?.Invoke("Standard mount failed, initiating recovery scan...");

        // Fall back to recovery scanning
        var scanner = new Ufs2RecoveryScanner(_diskSource, partitionStartSector, _progressCallback);
        var superblocks = await Task.Run(() => scanner.ScanForSuperblocks());

        if (superblocks.Count == 0)
        {
            return (false, null, null, "No valid superblocks found in recovery scan");
        }

        _progressCallback?.Invoke($"Found {superblocks.Count} potential superblock(s)");

        // Evaluate and rank superblocks by confidence
        var ranked = new List<(long Offset, Ufs2Superblock SB, double Confidence, string Reason)>();
        foreach (var (offset, sb) in superblocks)
        {
            var (isValid, reason, confidence) = scanner.VerifySuperblock(sb, _diskSource.TotalSize);
            ranked.Add((offset, sb, confidence, reason));
        }

        ranked = ranked.OrderByDescending(x => x.Confidence).ToList();

        _progressCallback?.Invoke($"Superblocks ranked by confidence:");
        foreach (var (offset, sb, confidence, reason) in ranked)
        {
            _progressCallback?.Invoke(
                $"  - 0x{offset:X8}: {(confidence * 100):F1}% confidence - {reason}");
        }

        // Use the highest confidence superblock
        var best = ranked.First();
        var recoveryFs = scanner.CreateRecoveryFilesystem(best.Offset, best.SB);

        return (true, null, recoveryFs,
            $"Using recovery filesystem at offset 0x{best.Offset:X8} ({best.Confidence * 100:F1}% confidence)");
    }

    /// <summary>
    /// Browse a recovery filesystem with graceful error handling.
    /// </summary>
    public List<FileTreeNode> BuildRecoveryFileTree(
        Ufs2FileSystemRecovery recoveryFs,
        long parentInodeNum = 2)
    {
        var nodes = new List<FileTreeNode>();

        var (success, inode, errorMsg) = recoveryFs.TryReadInode(parentInodeNum);
        if (!success || inode == null)
        {
            _progressCallback?.Invoke($"Error reading inode {parentInodeNum}: {errorMsg}");
            return nodes;
        }

        var entries = recoveryFs.TryReadDirectory(inode, out int corruptedEntries);

        if (corruptedEntries > 0)
            _progressCallback?.Invoke($"Warning: {corruptedEntries} corrupted directory entries skipped");

        foreach (var entry in entries)
        {
            if (entry.Name == "." || entry.Name == "..")
                continue;

            var (childSuccess, childInode, childError) = recoveryFs.TryReadInode(entry.InodeNumber);
            if (!childSuccess)
            {
                _progressCallback?.Invoke(
                    $"  Skipped '{entry.Name}' (inode {entry.InodeNumber}): {childError}");
                continue;
            }

            var node = new FileTreeNode
            {
                InodeNumber = entry.InodeNumber,
                Name = entry.Name,
                IsDirectory = childInode!.FileType == Ufs2FileType.Directory,
                FileSize = childInode.Size,
                ModifiedTime = childInode.ModifyDateTime,
                Inode = childInode,
                IsRecovered = true,
                RecoveryMode = true
            };

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Attempt to extract a file from recovery filesystem, handling partial reads.
    /// </summary>
    public async Task<(bool Success, long BytesExtracted, string Message)> TryExtractFileAsync(
        Ufs2FileSystemRecovery recoveryFs,
        Ufs2Inode inode,
        string outputPath)
    {
        try
        {
            using var output = System.IO.File.Create(outputPath);
            int bytesRead = 0;

            byte[] fileData = recoveryFs.TryReadInodeData(inode, out bytesRead);

            await output.WriteAsync(fileData, 0, fileData.Length);
            output.Flush();

            string message = inode.Size == bytesRead
                ? $"Successfully extracted {bytesRead} bytes"
                : $"Partially extracted: {bytesRead}/{inode.Size} bytes " +
                  $"({(bytesRead * 100.0 / inode.Size):F1}%)";

            return (bytesRead > 0, bytesRead, message);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Extract failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan filesystem for readable files and directories (recovery deep-scan).
    /// </summary>
    public async Task<(int ReadableFiles, int ReadableDirs, int Errors, List<string> SamplePaths)>
        PerformDeepScanAsync(Ufs2FileSystemRecovery recoveryFs)
    {
        int readableFiles = 0;
        int readableDirs = 0;
        int errors = 0;
        var samplePaths = new List<string>();

        // Scan first 10000 inodes
        const int maxInodesToScan = 10000;

        for (long i = 2; i < maxInodesToScan; i++)
        {
            var (success, inode, _) = recoveryFs.TryReadInode(i);
            if (!success)
            {
                errors++;
                continue;
            }

            if (inode!.FileType == Ufs2FileType.Directory)
                readableDirs++;
            else if (inode.FileType == Ufs2FileType.RegularFile)
                readableFiles++;

            if (samplePaths.Count < 20 && (inode.FileType == Ufs2FileType.Directory ||
                                           inode.FileType == Ufs2FileType.RegularFile))
            {
                samplePaths.Add($"Inode {i}: {inode.ModeString} {inode.Size} bytes");
            }

            if (i % 1000 == 0)
                _progressCallback?.Invoke($"Scanned {i}/{maxInodesToScan} inodes...");
        }

        return (readableFiles, readableDirs, errors, samplePaths);
    }
}

/// <summary>
/// File tree node with recovery metadata.
/// </summary>
public class FileTreeNode
{
    public long InodeNumber { get; set; }
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long FileSize { get; set; }
    public DateTime ModifiedTime { get; set; }
    public Ufs2Inode? Inode { get; set; }
    public bool IsRecovered { get; set; }
    public bool RecoveryMode { get; set; }

    public override string ToString()
    {
        string type = IsDirectory ? "[DIR]" : "[FILE]";
        string recovery = IsRecovered ? " [RECOVERED]" : "";
        return $"{type} {Name} ({FileSize} bytes){recovery}";
    }
}
