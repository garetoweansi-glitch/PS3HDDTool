/// <summary>
/// Mount the UFS2 filesystem from the GameOS partition.
/// Falls back to recovery mode if standard mount fails.
/// </summary>
private async Task MountFilesystemAsync()
{
    // If _fileSystem was already created (CBC path), just mount it
    if (_fileSystem != null && _diskLayout != null)
    {
        try
        {
            bool mounted = false;
            await Task.Run(() => { mounted = _fileSystem.Mount(); });

            if (!mounted)
            {
                StatusText = "UFS2 mount failed during detailed parse.";
                Log("ERROR: UFS2 mount failed during detailed superblock parse.");
                
                // Attempt recovery mode
                Log("Attempting recovery mode...");
                await TryRecoveryMountAsync();
                return;
            }

            IsFilesystemMounted = true;
            var sb2 = _fileSystem.Superblock!;
            FreeSpaceText = $"Free: {FormatSize(sb2.FreeSpaceBytes)}";
            Log($"UFS2 mounted: {sb2.CylinderGroups} CGs, block={sb2.BlockSize}, frag={sb2.FragmentSize}, ipg={sb2.InodesPerGroup}");
            Log($"  Free space: {FormatSize(sb2.FreeSpaceBytes)} ({sb2.FreeBlocks} blocks, {sb2.FreeFragments} frags, {sb2.FreeInodes} inodes)");

            if (DiskInfo != null)
            {
                DiskInfo.HasValidUfs2 = true;
                DiskInfo.VolumeName = sb2.VolumeName;
                DiskInfo.Status = "Filesystem mounted and ready.";
            }

            StatusText = "Filesystem mounted. Loading root directory...";
            await LoadDirectoryTreeAsync();
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"Mount error: {ex.Message}";
            Log($"ERROR mounting filesystem: {ex.Message}");
            
            // Try recovery on exception too
            Log("Attempting recovery mode due to exception...");
            await TryRecoveryMountAsync();
            return;
        }
    }

    if (_decryptedSource == null || _diskLayout == null) return;

    try
    {
        var gameOsPartition = _diskLayout.Partitions
            .FirstOrDefault(p => p.Type == Ps3PartitionType.GameOS);

        if (gameOsPartition == null)
        {
            StatusText = "No GameOS partition found.";
            Log("WARNING: Could not locate GameOS partition.");
            return;
        }

        Log($"Mounting UFS2 from partition at sector 0x{gameOsPartition.StartSector:X}...");

        bool mounted = false;
        await Task.Run(() =>
        {
            _fileSystem = new Ufs2FileSystem(_decryptedSource, gameOsPartition.StartSector);
            mounted = _fileSystem.Mount();
        });

        if (!mounted)
        {
            // This shouldn't happen since we already validated the superblock, but just in case
            StatusText = "UFS2 mount failed during detailed parse.";
            Log("ERROR: UFS2 mount failed during detailed superblock parse.");
            
            // Attempt recovery mode
            Log("Attempting recovery mode...");
            await TryRecoveryMountAsync();
            return;
        }

        IsFilesystemMounted = true;
        var sb = _fileSystem!.Superblock!;
        FreeSpaceText = $"Free: {FormatSize(sb.FreeSpaceBytes)}";
        Log($"UFS2 mounted: {sb.CylinderGroups} CGs, block={sb.BlockSize}, frag={sb.FragmentSize}, ipg={sb.InodesPerGroup}");
        Log($"  Free space: {FormatSize(sb.FreeSpaceBytes)} ({sb.FreeBlocks} blocks, {sb.FreeFragments} frags, {sb.FreeInodes} inodes)");

        if (DiskInfo != null)
        {
            DiskInfo.HasValidUfs2 = true;
            DiskInfo.VolumeName = sb.VolumeName;
            DiskInfo.Status = "Filesystem mounted and ready.";
        }

        StatusText = "Filesystem mounted. Loading root directory...";
        await LoadDirectoryTreeAsync();
    }
    catch (Exception ex)
    {
        StatusText = $"Mount error: {ex.Message}";
        Log($"ERROR mounting filesystem: {ex.Message}");
        
        // Try recovery on exception too
        Log("Attempting recovery mode due to exception...");
        await TryRecoveryMountAsync();
    }
}

/// <summary>
/// Emergency recovery mode: scan for displaced superblocks and mount corrupted filesystem.
/// </summary>
private async Task TryRecoveryMountAsync()
{
    if (_diskSource == null)
    {
        Log("Cannot enter recovery mode: no disk source available.");
        return;
    }

    try
    {
        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = "Entering recovery mode — scanning for superblocks...";
        LogSeparator();
        Log("RECOVERY MODE ACTIVATED");
        LogSeparator();

        var recoveryHelper = new RecoveryModeHelper(_diskSource, _partitionSector, msg => Log(msg));

        var (recSuccess, recFs, recErrorMsg) = await recoveryHelper.TryMountFilesystemAsync();

        if (!recSuccess)
        {
            StatusText = $"Recovery mode failed: {recErrorMsg}";
            Log($"RECOVERY FAILED: {recErrorMsg}");
            return;
        }

        if (recFs == null)
        {
            Log("Recovery mode returned null filesystem.");
            return;
        }

        Log($"Recovery filesystem created with superblock at offset 0x{recFs.Superblock.BlockSize}");

        // Build file tree from recovery filesystem
        StatusText = "Recovery mode: loading file tree...";
        var recoveryTree = await recoveryHelper.BuildRecoveryFileTree(recFs);

        // Perform deep scan to report recovery stats
        var (readable, corrupted, recoverable) = await recoveryHelper.PerformDeepScanAsync(recFs);
        
        Log($"RECOVERY STATISTICS:");
        Log($"  Readable files: {readable}");
        Log($"  Corrupted files: {corrupted}");
        Log($"  Recoverable bytes: {FormatSize(recoverable)}");
        Log($"  Recovery rate: {(readable + corrupted > 0 ? (double)readable / (readable + corrupted) * 100 : 0):F1}%");

        // Update UI with recovery results
        IsFilesystemMounted = true;
        FileTree.Clear();
        foreach (var node in recoveryTree.OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name))
            FileTree.Add(node);

        if (DiskInfo != null)
        {
            DiskInfo.HasValidUfs2 = true;
            DiskInfo.Status = $"RECOVERY MODE: {readable} readable files, {corrupted} corrupted";
        }

        StatusText = $"Recovery mode active — {readable} readable files found";
        Log($"Recovery mode: loaded {FileTree.Count} entries");
        LogSeparator();
    }
    catch (Exception ex)
    {
        StatusText = $"Recovery mode error: {ex.Message}";
        Log($"ERROR in recovery mode: {ex.Message}\n{ex.StackTrace}");
    }
    finally
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
    }
}
