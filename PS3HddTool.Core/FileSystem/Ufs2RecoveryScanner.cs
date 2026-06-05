using System.Buffers.Binary;
using System.Text;
using PS3HddTool.Core.Disk;

namespace PS3HddTool.Core.FileSystem;

/// <summary>
/// Recovery scanner for corrupted UFS2 filesystems.
/// Searches for valid superblocks at unusual offsets when standard locations fail.
/// Implements bad block detection and graceful degradation.
/// </summary>
public class Ufs2RecoveryScanner
{
    private readonly IDiskSource _disk;
    private readonly long _partitionOffsetBytes;
    private readonly Action<string>? _progressCallback;

    private const uint Ufs2Magic = 0x19540119;
    private const int SuperblockSize = 8192;
    private const int SectorSize = 512;

    public Ufs2RecoveryScanner(
        IDiskSource disk,
        long partitionStartSector,
        Action<string>? progressCallback = null)
    {
        _disk = disk;
        _partitionOffsetBytes = partitionStartSector * 512;
        _progressCallback = progressCallback;
    }

    /// <summary>
    /// Search for valid UFS2 superblocks at unusual offsets throughout the partition.
    /// Scans at various alignment boundaries and common displacement patterns.
    /// </summary>
    /// <returns>List of found superblocks with their offsets.</returns>
    public List<(long Offset, Ufs2Superblock Superblock)> ScanForSuperblocks()
    {
        var results = new List<(long, Ufs2Superblock)>();

        // Standard UFS2 superblock locations
        var offsets = new List<long>
        {
            65536,      // Standard location (0x10000)
            131072,     // Backup at 2x
            262144,     // Backup at 4x
            524288,     // Backup at 8x
        };

        // Add common displacement patterns for corrupted drives
        // Scan every 65536 bytes for first 10MB (common pattern in partially deleted systems)
        for (long offset = 0; offset < 10 * 1024 * 1024; offset += 65536)
        {
            if (!offsets.Contains(offset))
                offsets.Add(offset);
        }

        // Also scan at 4KB boundaries in the first 5MB (may catch accidentally shifted superblocks)
        for (long offset = 0; offset < 5 * 1024 * 1024; offset += 4096)
        {
            if (!offsets.Contains(offset))
                offsets.Add(offset);
        }

        offsets.Sort();

        foreach (var offset in offsets)
        {
            if (offset + SuperblockSize > _disk.TotalSize - _partitionOffsetBytes)
                break;

            try
            {
                _progressCallback?.Invoke($"Scanning offset 0x{offset:X8}...");

                byte[] sbData = _disk.ReadBytes(_partitionOffsetBytes + offset, SuperblockSize);
                var superblock = Ufs2Superblock.Parse(sbData);

                if (superblock.IsValid)
                {
                    // Basic sanity checks
                    if (superblock.BlockSize > 0 && superblock.BlockSize <= 65536 &&
                        superblock.FragmentSize > 0 && superblock.FragmentSize <= superblock.BlockSize &&
                        superblock.InodesPerGroup > 0 &&
                        superblock.CylinderGroups > 0)
                    {
                        _progressCallback?.Invoke(
                            $"Found valid superblock at offset 0x{offset:X8} " +
                            $"(BlockSize={superblock.BlockSize}, FragSize={superblock.FragmentSize}, " +
                            $"CGs={superblock.CylinderGroups}, Vol='{superblock.VolumeName}')");

                        results.Add((offset, superblock));
                    }
                }
            }
            catch
            {
                // Continue scanning on read errors
            }
        }

        return results;
    }

    /// <summary>
    /// Verify superblock validity and estimate recovery success.
    /// </summary>
    public (bool IsValid, string Reason, double ConfidenceScore) VerifySuperblock(
        Ufs2Superblock sb, long diskSize)
    {
        if (!sb.IsValid)
            return (false, "Invalid magic number", 0.0);

        var issues = new List<string>();
        double confidence = 1.0;

        // Check block size
        if (sb.BlockSize <= 0 || sb.BlockSize > 65536)
        {
            issues.Add($"Invalid BlockSize: {sb.BlockSize}");
            confidence -= 0.3;
        }

        // Check fragment size
        if (sb.FragmentSize <= 0 || sb.FragmentSize > sb.BlockSize)
        {
            issues.Add($"Invalid FragmentSize: {sb.FragmentSize}");
            confidence -= 0.3;
        }

        // Check geometry
        if (sb.InodesPerGroup <= 0)
        {
            issues.Add("Invalid InodesPerGroup");
            confidence -= 0.2;
        }

        if (sb.CylinderGroups <= 0 || sb.CylinderGroups > 10000)
        {
            issues.Add($"Invalid CylinderGroups: {sb.CylinderGroups}");
            confidence -= 0.2;
        }

        // Check total fragments vs disk size
        long expectedBytes = sb.TotalFragments * sb.FragmentSize;
        if (expectedBytes > diskSize)
        {
            issues.Add($"TotalFragments {sb.TotalFragments} would exceed disk size");
            confidence -= 0.1;
        }

        // Positive indicators
        if (!string.IsNullOrEmpty(sb.VolumeName))
            confidence += 0.1; // Having a volume name is a good sign

        if (sb.FreeBlocks <= sb.TotalFragments / sb.BlockSize && sb.FreeBlocks >= 0)
            confidence += 0.05;

        confidence = Math.Max(0.0, Math.Min(1.0, confidence));

        string reason = issues.Count > 0
            ? string.Join("; ", issues)
            : "Superblock appears valid";

        return (confidence > 0.5, reason, confidence);
    }

    /// <summary>
    /// Create a recovery-mode filesystem that skips bad inodes and blocks.
    /// </summary>
    public Ufs2FileSystemRecovery CreateRecoveryFilesystem(
        long superblockOffset,
        Ufs2Superblock superblock)
    {
        return new Ufs2FileSystemRecovery(_disk, _partitionOffsetBytes, superblockOffset, superblock, _progressCallback);
    }
}

/// <summary>
/// Recovery-mode UFS2 filesystem reader.
/// Gracefully handles corrupted inodes, bad blocks, and missing indirect blocks.
/// </summary>
public class Ufs2FileSystemRecovery
{
    private readonly IDiskSource _disk;
    private readonly long _partitionOffsetBytes;
    private readonly long _superblockOffsetBytes;
    public Ufs2Superblock Superblock { get; }
    private readonly Action<string>? _progressCallback;

    private readonly HashSet<long> _badInodes = new();
    private readonly HashSet<long> _badBlocks = new();

    public Ufs2FileSystemRecovery(
        IDiskSource disk,
        long partitionOffsetBytes,
        long superblockOffsetBytes,
        Ufs2Superblock superblock,
        Action<string>? progressCallback = null)
    {
        _disk = disk;
        _partitionOffsetBytes = partitionOffsetBytes;
        _superblockOffsetBytes = superblockOffsetBytes;
        Superblock = superblock;
        _progressCallback = progressCallback;
    }

    /// <summary>
    /// Attempt to read an inode, gracefully handling corruption.
    /// </summary>
    public (bool Success, Ufs2Inode? Inode, string ErrorMessage) TryReadInode(long inodeNumber)
    {
        if (_badInodes.Contains(inodeNumber))
            return (false, null, "Inode previously marked as bad");

        try
        {
            long inodesPerGroup = Superblock.InodesPerGroup;
            long group = inodeNumber / inodesPerGroup;
            long indexInGroup = inodeNumber % inodesPerGroup;

            long cgOffset = _partitionOffsetBytes + _superblockOffsetBytes +
                            (group * Superblock.FragsPerGroup * Superblock.FragmentSize);
            long inodeTableOffset = cgOffset + (Superblock.InodeBlockOffset * Superblock.FragmentSize);
            long inodeOffset = inodeTableOffset + (indexInGroup * Superblock.InodeSize);

            // Bounds check
            if (inodeOffset < 0 || inodeOffset + Superblock.InodeSize > _disk.TotalSize)
            {
                _badInodes.Add(inodeNumber);
                return (false, null, "Inode offset out of bounds");
            }

            byte[] inodeData = _disk.ReadBytes(inodeOffset, (int)Superblock.InodeSize);
            var inode = Ufs2Inode.Parse(inodeData, inodeNumber);

            // Basic sanity check
            if (inode.Mode == 0 && inode.Size == 0 && inode.LinkCount == 0)
            {
                _badInodes.Add(inodeNumber);
                return (false, null, "Inode appears uninitialized (all zeros)");
            }

            return (true, inode, "");
        }
        catch (Exception ex)
        {
            _badInodes.Add(inodeNumber);
            return (false, null, $"Exception reading inode: {ex.Message}");
        }
    }

    /// <summary>
    /// Read directory entries, skipping corrupted entries.
    /// </summary>
    public List<Ufs2DirectoryEntry> TryReadDirectory(Ufs2Inode dirInode, out int corruptedEntries)
    {
        corruptedEntries = 0;
        var entries = new List<Ufs2DirectoryEntry>();

        if (dirInode.FileType != Ufs2FileType.Directory)
            return entries;

        try
        {
            byte[] dirData = TryReadInodeData(dirInode, out int bytesRead);
            if (bytesRead == 0)
                return entries;

            int offset = 0;
            while (offset < dirData.Length)
            {
                if (offset + 8 > dirData.Length)
                    break;

                try
                {
                    uint ino = BinaryPrimitives.ReadUInt32BigEndian(dirData.AsSpan(offset));
                    ushort recLen = BinaryPrimitives.ReadUInt16BigEndian(dirData.AsSpan(offset + 4));
                    byte fileType = dirData[offset + 6];
                    byte nameLen = dirData[offset + 7];

                    if (recLen == 0 || recLen > 512)
                        break; // Sanity check on record length

                    if (ino != 0 && nameLen > 0 && nameLen < 256 && offset + 8 + nameLen <= dirData.Length)
                    {
                        string name = Encoding.ASCII.GetString(dirData, offset + 8, nameLen);
                        entries.Add(new Ufs2DirectoryEntry
                        {
                            InodeNumber = ino,
                            Name = name,
                            FileType = (Ufs2DirEntryType)fileType,
                            RecordLength = recLen
                        });
                    }

                    offset += recLen;
                }
                catch
                {
                    corruptedEntries++;
                    offset += 512; // Skip ahead on parse error
                }
            }
        }
        catch
        {
            _progressCallback?.Invoke("Failed to read directory data");
        }

        return entries;
    }

    /// <summary>
    /// Attempt to read inode data, gracefully skipping bad blocks.
    /// </summary>
    public byte[] TryReadInodeData(Ufs2Inode inode, out int bytesRead)
    {
        bytesRead = 0;
        long fileSize = inode.Size;
        if (fileSize == 0)
            return Array.Empty<byte>();

        long blockSize = Superblock.BlockSize;
        var data = new MemoryStream();

        // Direct blocks
        for (int i = 0; i < 12 && data.Length < fileSize; i++)
        {
            long blockAddr = inode.DirectBlocks[i];
            if (blockAddr == 0)
                continue;

            if (TryReadBlock(blockAddr, out byte[]? blockData))
            {
                int toWrite = (int)Math.Min(blockData!.Length, fileSize - data.Length);
                data.Write(blockData, 0, toWrite);
            }
        }

        // Indirect blocks (attempt, but skip on errors)
        if (data.Length < fileSize && inode.IndirectBlock != 0)
            TryReadIndirectBlocks(inode.IndirectBlock, 1, data, fileSize);

        if (data.Length < fileSize && inode.DoubleIndirectBlock != 0)
            TryReadIndirectBlocks(inode.DoubleIndirectBlock, 2, data, fileSize);

        if (data.Length < fileSize && inode.TripleIndirectBlock != 0)
            TryReadIndirectBlocks(inode.TripleIndirectBlock, 3, data, fileSize);

        bytesRead = (int)data.Length;
        return data.ToArray();
    }

    /// <summary>
    /// Attempt to read a block with bounds checking.
    /// </summary>
    private bool TryReadBlock(long blockAddress, out byte[]? data)
    {
        data = null;

        if (_badBlocks.Contains(blockAddress))
            return false;

        try
        {
            long offset = _partitionOffsetBytes + _superblockOffsetBytes +
                          (blockAddress * Superblock.FragmentSize);

            if (offset < 0 || offset + Superblock.BlockSize > _disk.TotalSize)
            {
                _badBlocks.Add(blockAddress);
                return false;
            }

            data = _disk.ReadBytes(offset, (int)Superblock.BlockSize);
            return true;
        }
        catch
        {
            _badBlocks.Add(blockAddress);
            return false;
        }
    }

    /// <summary>
    /// Recursively read indirect block chains with error recovery.
    /// </summary>
    private void TryReadIndirectBlocks(long blockAddr, int level, MemoryStream data, long maxSize)
    {
        if (blockAddr == 0 || data.Length >= maxSize)
            return;

        if (!TryReadBlock(blockAddr, out byte[]? indirectBlockData))
            return;

        int pointersPerBlock = (int)(Superblock.BlockSize / 8);

        for (int i = 0; i < pointersPerBlock && data.Length < maxSize; i++)
        {
            try
            {
                long pointer = BinaryPrimitives.ReadInt64BigEndian(
                    indirectBlockData!.AsSpan(i * 8));

                if (pointer == 0)
                    continue;

                if (level == 1)
                {
                    if (TryReadBlock(pointer, out byte[]? blockData))
                    {
                        int toWrite = (int)Math.Min(blockData!.Length, maxSize - data.Length);
                        data.Write(blockData, 0, toWrite);
                    }
                }
                else
                {
                    TryReadIndirectBlocks(pointer, level - 1, data, maxSize);
                }
            }
            catch
            {
                // Continue to next pointer
            }
        }
    }
}
