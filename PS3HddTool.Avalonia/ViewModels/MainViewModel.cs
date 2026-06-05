using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PS3HddTool.Core;
using PS3HddTool.Core.Crypto;
using PS3HddTool.Core.Disk;
using PS3HddTool.Core.FileSystem;
using PS3HddTool.Core.Models;

namespace PS3HddTool.Avalonia.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private IDiskSource? _diskSource;
    private DecryptedDiskSource? _decryptedSource;
    private Ufs2FileSystem? _fileSystem;
    private Ps3DiskLayout? _diskLayout;
    private readonly DriveProfileDatabase _driveProfiles = new();
    
    // Recovery mode state
    private RecoveryModeHelper? _recoveryModeHelper;
    private Ufs2FileSystemRecovery? _recoveryFileSystem;

    // Stored for reopening with write access
    private string? _physicalDrivePath;
    private long _physicalDriveSize;
    private byte[]? _cbcKey;
    private bool _cbcBswap;
    private long _partitionSector;
    private byte[]? _xtsDataKey;
    private byte[]? _xtsTweakKey;
    private bool _xtsBswap;
    private bool _isXts; // true = XTS (Slim/NOR), false = CBC (Fat NAND)
    public string DetectedEncryptionType => _isXts ? "XTS-128" : IsDecrypted ? "CBC-192" : "";
    public string EncryptionHint { get; set; } = ""; // Set by key database to skip wrong scan

    [ObservableProperty] private string _statusText = "Ready — Open a disk image or select a physical drive.";
    [ObservableProperty] private string _eidRootKeyHex = "";
    [ObservableProperty] private bool _isDiskOpen;
    [ObservableProperty] private bool _isDecrypted;
    [ObservableProperty] private bool _isFilesystemMounted;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private DiskInfo? _diskInfo;
    [ObservableProperty] private FileTreeNode? _selectedNode;
    [ObservableProperty] private global::Avalonia.Media.Imaging.Bitmap? _imagePreview;
    [ObservableProperty] private bool _hasImagePreview;
    [ObservableProperty] private string _freeSpaceText = "";
    [ObservableProperty] private bool _isRecoveryMode = false;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    partial void OnSelectedNodeChanged(FileTreeNode? value)
    {
        if (value != null && value.IsDirectory && !value.ChildrenLoaded)
        {
            _ = ExpandNodeAsync(value);
        }

        // Load image preview if it's an image file
        if (value != null && !value.IsDirectory && ImageExtensions.Contains(Path.GetExtension(value.Name)))
        {
            _ = LoadImagePreviewAsync(value);
        }
        else
        {
            ImagePreview = null;
            HasImagePreview = false;
        }
    }

    private async Task LoadImagePreviewAsync(FileTreeNode node)
    {
        if (_fileSystem == null) return;

        try
        {
            // Only preview files under 10MB to avoid memory issues
            if (node.Size > 10 * 1024 * 1024)
            {
                HasImagePreview = false;
                return;
            }

            byte[]? imageData = null;
            await Task.Run(() =>
            {
                var inode = _fileSystem.ReadInode(node.InodeNumber);
                imageData = _fileSystem.ReadInodeData(inode);
            });

            if (imageData != null && imageData.Length > 0)
            {
                using var ms = new MemoryStream(imageData);
                ImagePreview = new global::Avalonia.Media.Imaging.Bitmap(ms);
                HasImagePreview = true;
            }
        }
        catch
        {
            ImagePreview = null;
            HasImagePreview = false;
        }
    }

    public ObservableCollection<FileTreeNode> FileTree { get; } = new();
    public ObservableCollection<Ps3Partition> Partitions { get; } = new();
    public ObservableCollection<string> LogMessages { get; } = new();

    /// <summary>
    /// Open a disk image file.
    /// </summary>
    [RelayCommand]
    public async Task OpenImageAsync(string filePath)
    {
        try
        {
            IsBusy = true;
            StatusText = $"Opening {Path.GetFileName(filePath)}...";
            Log($"Opening image: {filePath}");

            await Task.Run(() =>
            {
                _diskSource?.Dispose();
                _decryptedSource?.Dispose();

                _diskSource = new ImageDiskSource(filePath);
            });

            IsDiskOpen = true;
            IsDecrypted = false;
            IsFilesystemMounted = false;
            IsRecoveryMode = false;
            FileTree.Clear();
            Partitions.Clear();

            DiskInfo = new DiskInfo
            {
                Source = _diskSource!.Description,
                TotalSize = _diskSource.TotalSize,
                TotalSizeFormatted = FormatSize(_diskSource.TotalSize),
                IsEncrypted = true,
                Status = "Disk opened — enter EID Root Key and decrypt."
            };

            StatusText = $"Disk image opened: {FormatSize(_diskSource.TotalSize)}. Enter EID Root Key to decrypt.";
            Log($"Image loaded: {_diskSource.SectorCount} sectors, {FormatSize(_diskSource.TotalSize)}");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Open a physical drive.
    /// </summary>
    [RelayCommand]
    public async Task OpenPhysicalDriveAsync((string Path, long Size) drive)
    {
        try
        {
            IsBusy = true;
            StatusText = $"Opening {drive.Path}...";
            Log($"Opening physical drive: {drive.Path}");

            await Task.Run(() =>
            {
                // Check for 4K native drives — incompatible with PS3
                var (logical, physical) = PhysicalDiskSource.DetectSectorSizes(drive.Path);
                if (logical > 0)
                    Log($"Drive sector sizes: logical={logical}, physical={physical} ({(logical == 512 && physical == 512 ? "512n" : logical == 512 ? "512e" : "4Kn")})");

                if (logical >= 4096)
                    throw new InvalidOperationException(
                        $"This drive uses 4K native sectors (logical={logical}, physical={physical}). " +
                        "The PS3 requires 512-byte logical sectors (512n or 512e). " +
                        "4K native drives will cause filesystem corruption on the PS3. " +
                        "Use a drive with 512-byte logical sector support.");

                _diskSource?.Dispose();
                _decryptedSource?.Dispose();

                _diskSource = new PhysicalDiskSource(drive.Path, drive.Size, writable: true);
                _physicalDrivePath = drive.Path;
                _physicalDriveSize = drive.Size;
            });

            IsDiskOpen = true;
            IsDecrypted = false;
            IsFilesystemMounted = false;
            IsRecoveryMode = false;
            FileTree.Clear();
            Partitions.Clear();

            DiskInfo = new DiskInfo
            {
                Source = _diskSource!.Description,
                TotalSize = _diskSource.TotalSize,
                TotalSizeFormatted = FormatSize(_diskSource.TotalSize),
                IsEncrypted = true,
                Status = "Drive opened — enter EID Root Key and decrypt."
            };

            StatusText = $"Physical drive opened. Enter EID Root Key to decrypt.";
            Log($"Drive opened: {_diskSource.SectorCount} sectors, {FormatSize(_diskSource.TotalSize)}");
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Candidate partition start sectors to scan for UFS2 superblock.
    /// Covers known PS3 layouts across firmware versions.
    /// </summary>
    private static readonly long[] CandidatePartitionStarts =
    {
        0x400018, 0x400020, 0x400000, 0x3FFFF8, // dev_hdd0 (after ~2GB dev_hdd1)
        // Slim/NOR: VFLASH=0x80000 sectors, GameOS starts after VFLASH + padding
        0x80018, 0x80020, 0x80028, 0x80010, 0x80008, 0x80030,
        0, 2, 8, 16, 0x18, 0x20, 0x22, 0x28, 0x30, 0x40, 0x80,
        128, 256, 512, 1024, 
        0x800, 0x1000, 0x2000, 0x4000, 0x8000,
        0x10000, 0x20000, 
        0x40000, 0x80000,
        0x100000, 0x200000, 0x400000, 0x800000,
    };

    /// <summary>
    /// Decrypt the disk using the provided EID Root Key.
    /// Tries all known key derivation methods and scans for UFS2 superblock.
    /// </summary>
    [RelayCommand]
    public async Task DecryptAsync()
    {
        if (_diskSource == null || string.IsNullOrWhiteSpace(EidRootKeyHex))
        {
            StatusText = "Please open a disk and enter the EID Root Key first.";
            return;
        }

        try
        {
            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusText = "Parsing EID Root Key...";

            // Start fresh log section
            LogSeparator();
            Log("PS3 HDD Tool — Decryption Attempt");
            LogSeparator();

            // Run XTS self-test first
            bool xtsOk = AesXts128.SelfTest();
            Log($"AES-XTS-128 self-test: {(xtsOk ? "PASSED" : "FAILED")}");
            if (!xtsOk)
            {
                StatusText = "FATAL: AES-XTS implementation failed self-test!";
                return;
            }

            // Verify optimized CBC-192 encrypt matches original
            {
                byte[] testKey = new byte[24];
                Array.Fill(testKey, (byte)0xAB);
                byte[] testData = new byte[1024 * 1024];
                new Random(42).NextBytes(testData);
                using var cbc = new AesCbc192(testKey);
                byte[] enc1 = cbc.EncryptSectors(testData);
                byte[] enc2 = cbc.EncryptSectorsOriginal(testData);
                bool cbcOk = enc1.AsSpan().SequenceEqual(enc2);
                Log($"AES-CBC-192 optimize self-test: {(cbcOk ? "PASSED" : "FAILED")} (1MB / {testData.Length / 512} sectors)");
                if (!cbcOk)
                {
                    StatusText = "FATAL: Optimized CBC-192 encryption produces different output!";
                    return;
                }
            }

            Log($"Disk: {_diskSource.Description}");
            Log($"Disk size: {_diskSource.TotalSize} bytes ({FormatSize(_diskSource.TotalSize)})");
            Log($"Sector count: {_diskSource.SectorCount}");

            byte[] eidRootKey;
            bool isPreDerivedXts = EidRootKeyHex.StartsWith("HDDKEY:", StringComparison.OrdinalIgnoreCase);
            bool isPreDerivedCbc = EidRootKeyHex.StartsWith("CBCKEY:", StringComparison.OrdinalIgnoreCase);
            bool isPartial = EidRootKeyHex.StartsWith("PARTIAL:", StringComparison.OrdinalIgnoreCase);

            List<(string Method, byte[] DataKey, byte[] TweakKey)> allKeys;
            Ps3DerivedKeys? cbcKeys = null;

            if (isPartial)
            {
                StatusText = "Only one key component imported. Select both data + tweak files at once.";
                Log("Cannot decrypt: only a single key component was imported.");
                Log("  Re-import by selecting BOTH key files together (multi-select in file dialog).");
                return;
            }
            else if (isPreDerivedCbc)
            {
                // Pre-derived Fat CBC-192 hdd_key (48 bytes: 24B data + 24B tweak)
                string hex = EidRootKeyHex.Substring(7).Replace("-", "").Replace(" ", "").Trim();
                if (hex.Length != 96)
                {
                    StatusText = $"Invalid CBC key: expected 96 hex chars (48 bytes), got {hex.Length}";
                    return;
                }
                byte[] hddKey = new byte[48];
                for (int i = 0; i < 48; i++)
                    hddKey[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

                byte[] dk24 = hddKey[..24];
                byte[] tk24 = hddKey[24..48];
                byte[] dk16 = hddKey[..16];
                byte[] tk16 = hddKey[24..40];

                Log($"Using pre-derived Fat HDD key (CBC-192):");
                Log($"  Data key  (24B): {BitConverter.ToString(dk24)}");
                Log($"  Tweak key (24B): {BitConverter.ToString(tk24)}");

                // Primary: CBC-192 with 24-byte key
                cbcKeys = new Ps3DerivedKeys { AtaDataKey = dk24, AtaTweakKey = tk24 };

                // Also try as XTS-128 with first 16 bytes of each half
                allKeys = new List<(string, byte[], byte[])>
                {
                    ("Pre-derived XTS-128 from CBC key", dk16, tk16),
                    ("Pre-derived XTS-128 from CBC key (reversed)", tk16, dk16)
                };

                eidRootKey = new byte[48]; // dummy
            }
            else if (isPreDerivedXts)
            {
                // Pre-derived hdd_key.bin imported directly (32 bytes: 16B data + 16B tweak)
                string hex = EidRootKeyHex.Substring(7).Replace("-", "").Replace(" ", "").Trim();
                if (hex.Length != 64)
                {
                    StatusText = $"Invalid pre-derived key: expected 64 hex chars, got {hex.Length}";
                    return;
                }
                byte[] hddKey = new byte[32];
                for (int i = 0; i < 32; i++)
                    hddKey[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

                byte[] dk = hddKey[..16];
                byte[] tk = hddKey[16..32];

                Log($"Using pre-derived HDD key (XTS-128):");
                Log($"  Data key:  {BitConverter.ToString(dk)}");
                Log($"  Tweak key: {BitConverter.ToString(tk)}");

                allKeys = new List<(string, byte[], byte[])>
                {
                    ("Pre-derived XTS-128 (hdd_key.bin)", dk, tk),
                    ("Pre-derived XTS-128 reversed", tk, dk)
                };

                // Also try as CBC-192 by padding to 24 bytes
                byte[] cbcAttempt = new byte[24];
                Buffer.BlockCopy(dk, 0, cbcAttempt, 0, 16);
                Buffer.BlockCopy(tk, 0, cbcAttempt, 16, 8);
                cbcKeys = new Ps3DerivedKeys { AtaDataKey = cbcAttempt, AtaTweakKey = cbcAttempt };

                eidRootKey = new byte[48]; // dummy, not used
            }
            else
            {
                // Standard EID Root Key flow
                try
                {
                    eidRootKey = Ps3KeyDerivation.ParseEidRootKey(EidRootKeyHex);
                }
                catch (Exception ex)
                {
                    StatusText = $"Invalid EID Root Key: {ex.Message}";
                    Log($"Key parse error: {ex.Message}");
                    return;
                }

                Log($"EID Root Key accepted: {Ps3KeyDerivation.DescribeKey(eidRootKey)}");
                Log($"  ERK key (bytes 0-31):  {BitConverter.ToString(eidRootKey[..32])}");
                Log($"  ERK IV  (bytes 32-47): {BitConverter.ToString(eidRootKey[32..48])}");

                StatusText = "Deriving all possible key combinations...";

                allKeys = Ps3KeyDerivation.DeriveAllPossibleKeys(eidRootKey);

                foreach (var (method, dk, tk) in allKeys)
                {
                    Log($"  Key set [{method}]:");
                    Log($"    Data key:  {BitConverter.ToString(dk)}");
                    Log($"    Tweak key: {BitConverter.ToString(tk)}");
                }

                cbcKeys = Ps3KeyDerivation.DeriveKeysFatNand(eidRootKey);
                Log($"  CBC-192 ATA data key (24B): {BitConverter.ToString(cbcKeys.AtaDataKey)}");
            }

            // Dump raw sector 0 before any decryption
            byte[] rawSector0 = _diskSource.ReadSectors(0, 1);
            Log($"Raw sector 0 (first 64 bytes): {BitConverter.ToString(rawSector0[..64])}");

            // ─── CACHED PROFILE: try instant decrypt from previous session ───
            string driveFingerprint = DriveProfileDatabase.ComputeFingerprint(rawSector0);
            var cachedProfile = _driveProfiles.Find(driveFingerprint);

            if (cachedProfile != null)
            {
                Log($"Found cached drive profile: {cachedProfile.Label}");
                Log($"  Encryption: {cachedProfile.EncryptionType}, bswap16={cachedProfile.Bswap16}, partition=0x{cachedProfile.PartitionSector:X}");

                bool cacheHit = false;
                try
                {
                    if (cachedProfile.EncryptionType == "CBC-192")
                    {
                        byte[] dataKey = Convert.FromHexString(cachedProfile.DataKeyHex);
                        var cachedCbc = new DecryptedDiskSourceCbc(
                            new NonDisposingDiskSource(_diskSource), dataKey, cachedProfile.Bswap16);

                        // Verify UFS2 superblock is still there
                        long sbOff = (cachedProfile.PartitionSector * 512) + 65536;
                        byte[] sbData = cachedCbc.ReadBytes(sbOff, 8192);
                        uint sbMagic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                               (sbData[0x55E] << 8) | sbData[0x55F]);
                        if (sbMagic == 0x19540119)
                        {
                            _cbcKey = (byte[])dataKey.Clone();
                            _cbcBswap = cachedProfile.Bswap16;
                            _partitionSector = cachedProfile.PartitionSector;
                            _isXts = false;
                            _fileSystem = new Ufs2FileSystem(cachedCbc, cachedProfile.PartitionSector);
                            cacheHit = true;
                            Log("  *** CACHE HIT: Instant decrypt successful! ***");
                        }
                        else
                        {
                            cachedCbc.Dispose();
                            Log($"  Cache miss: UFS2 magic 0x{sbMagic:X8} != 0x19540119. Full scan needed.");
                        }
                    }
                    else // XTS-128
                    {
                        byte[] dataKey = Convert.FromHexString(cachedProfile.DataKeyHex);
                        byte[] tweakKey = Convert.FromHexString(cachedProfile.TweakKeyHex);
                        var cachedXts = new DecryptedDiskSource(
                            new NonDisposingDiskSource(_diskSource), dataKey, tweakKey, cachedProfile.Bswap16);

                        long sbOff = (cachedProfile.PartitionSector * 512) + 65536;
                        byte[] sbData = cachedXts.ReadBytes(sbOff, 8192);
                        uint sbMagic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                               (sbData[0x55E] << 8) | sbData[0x55F]);
                        if (sbMagic == 0x19540119)
                        {
                            _xtsDataKey = (byte[])dataKey.Clone();
                            _xtsTweakKey = (byte[])tweakKey.Clone();
                            _xtsBswap = cachedProfile.Bswap16;
                            _partitionSector = cachedProfile.PartitionSector;
                            _isXts = true;
                            _decryptedSource = cachedXts as DecryptedDiskSource;
                            _fileSystem = new Ufs2FileSystem(cachedXts, cachedProfile.PartitionSector);
                            cacheHit = true;
                            Log("  *** CACHE HIT: Instant decrypt successful! ***");
                        }
                        else
                        {
                            cachedXts.Dispose();
                            Log($"  Cache miss: UFS2 magic 0x{sbMagic:X8} != 0x19540119. Full scan needed.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  Cache validation failed: {ex.Message}. Full scan needed.");
                }

                if (cacheHit)
                {
                    // Skip straight to mounting
                    IsDecrypted = true;
                    EncryptionHint = cachedProfile.EncryptionType;

                    _diskLayout = new Ps3DiskLayout { Partitions = new List<Ps3Partition>() };
                    if (cachedProfile.PartitionSector > 0)
                    {
                        _diskLayout.Partitions.Add(new Ps3Partition
                        {
                            Index = 0, Name = "System", StartSector = 0,
                            SectorCount = cachedProfile.PartitionSector, Type = Ps3PartitionType.System
                        });
                    }
                    _diskLayout.Partitions.Add(new Ps3Partition
                    {
                        Index = _diskLayout.Partitions.Count, Name = "GameOS (UFS2)",
                        StartSector = cachedProfile.PartitionSector,
                        SectorCount = _diskSource.SectorCount - cachedProfile.PartitionSector,
                        Type = Ps3PartitionType.GameOS
                    });
                    _diskLayout.DataRegionStartSector = cachedProfile.PartitionSector;
                    _diskLayout.DataRegionSectorCount = _diskSource.SectorCount - cachedProfile.PartitionSector;

                    Partitions.Clear();
                    foreach (var p in _diskLayout.Partitions)
                        Partitions.Add(p);

                    if (DiskInfo != null)
                    {
                        DiskInfo.IsDecrypted = true;
                        DiskInfo.PartitionCount = Partitions.Count;
                        DiskInfo.Status = "Decrypted (cached) — mounting UFS2 filesystem...";
                    }

                    // Update last-used timestamp
                    cachedProfile.LastUsed = DateTime.UtcNow;
                    _driveProfiles.Save(cachedProfile);

                    StatusText = "Instant decrypt from cache. Mounting filesystem...";
                    await MountFilesystemAsync();

                    IsBusy = false;
                    IsProgressIndeterminate = false;
                    return;
                }
                else
                {
                    // Remove stale cache entry
                    _driveProfiles.Remove(driveFingerprint);
                }
            }

            // ─── No cache hit — proceed with full scan ───
            bool hasErkDerivedCbc = !isPreDerivedXts && !isPreDerivedCbc;
            Log($"Will try {(hasErkDerivedCbc ? "CBC-192 (NAND) + " : isPreDerivedCbc ? "CBC-192 (pre-derived) + " : "")}{allKeys.Count} XTS key method(s) x 2 bswap modes x {CandidatePartitionStarts.Length} partition offset(s)...");

            bool found = false;
            string foundMethod = "";
            long foundPartitionSector = 0;
            bool foundBswap = false;
            bool foundCbc = false;

            await Task.Run(() =>
            {
                // ─── FAST PATH: Try known Fat NAND config (CBC-192 + bswap16 + common offsets) ───
                if (EncryptionHint != "XTS-128")
                {
                Log("Trying fast path: CBC-192 + bswap16 + common partition offsets...");
                try
                {
                    var fastCandidate = new DecryptedDiskSourceCbc(
                        new NonDisposingDiskSource(_diskSource), cbcKeys.AtaDataKey, true);
                    
                    byte[] fastSec0 = fastCandidate.ReadSectors(0, 1);
                    uint fm1 = (uint)((fastSec0[0x14] << 24) | (fastSec0[0x15] << 16) |
                                       (fastSec0[0x16] << 8) | fastSec0[0x17]);
                    uint fm2 = (uint)((fastSec0[0x1C] << 24) | (fastSec0[0x1D] << 16) |
                                       (fastSec0[0x1E] << 8) | fastSec0[0x1F]);
                    
                    if (fm1 == 0x0FACE0FF && fm2 == 0xDEADFACE)
                    {
                        // Partition table valid! Try common UFS2 offsets
                        foreach (long fastPart in new long[] { 0x20, 0x18, 0x10, 0x28, 0x30, 0x08 })
                        {
                            long sbOff = (fastPart * 512) + 65536;
                            if (sbOff + 8192 > _diskSource.TotalSize) continue;
                            byte[] sbData = fastCandidate.ReadBytes(sbOff, 8192);
                            uint sbMagic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                                   (sbData[0x55E] << 8) | sbData[0x55F]);
                            
                            if (sbMagic == 0x19540119)
                            {
                                found = true;
                                foundMethod = $"AES-CBC-192 NAND [bswap16=True] (fast path @ 0x{fastPart:X})";
                                foundPartitionSector = fastPart;
                                foundBswap = true;
                                foundCbc = true;
                                Log($"  *** FAST PATH SUCCESS: Fat NAND detected at sector 0x{fastPart:X}! ***");
                                break;
                            }
                        }
                    }
                    fastCandidate.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"  Fast path failed: {ex.Message}");
                }
                } // end if (hint != XTS)
                else
                {
                    Log("Skipping CBC fast path (hint: XTS-128).");
                }

                if (found)
                {
                    // Skip the full scan — log what we found
                    Log($"SUCCESS: UFS2 superblock found via fast path!");
                    Log($"  Key method: {foundMethod}");
                    Log($"  Partition start: sector 0x{foundPartitionSector:X} ({foundPartitionSector})");
                }
                else
                {
                    Log("Fast path didn't match, falling back to full scan...");
                }

                // ─── FULL SCAN: Try all combinations (only if fast path failed) ───
                if (!found)
                {

                bool skipCbc = EncryptionHint == "XTS-128";
                bool skipXts = EncryptionHint == "CBC-192";

                if (skipCbc)
                    Log("  Hint: XTS-128 — skipping CBC-192 scan.");
                if (skipXts)
                    Log("  Hint: CBC-192 — skipping XTS-128 scan.");

                // ─── Try CBC-192 first (Fat NAND models like CECHA/B/C/E) ───
                if (!skipCbc)
                foreach (bool useBswap in new[] { false, true })
                {
                    if (found) break;

                    string label = $"AES-CBC-192 NAND [bswap16={useBswap}]";
                    var candidate = new DecryptedDiskSourceCbc(
                        new NonDisposingDiskSource(_diskSource), cbcKeys.AtaDataKey, useBswap);

                    try
                    {
                        byte[] sector0 = candidate.ReadSectors(0, 1);
                        uint magic1 = (uint)((sector0[0x14] << 24) | (sector0[0x15] << 16) |
                                              (sector0[0x16] << 8) | sector0[0x17]);
                        uint magic2 = (uint)((sector0[0x1C] << 24) | (sector0[0x1D] << 16) |
                                              (sector0[0x1E] << 8) | sector0[0x1F]);

                        bool valid = (magic1 == 0x0FACE0FF || magic2 == 0xDEADFACE);
                        Log($"  [{label}] Sector 0 magic: {magic1:X8} / {magic2:X8}" +
                            (valid ? " *** MATCH! ***" : " (expected 0FACE0FF/DEADFACE)"));

                        // Also log first 32 bytes for debug
                        Log($"    First 32 bytes: {BitConverter.ToString(sector0, 0, 32)}");

                        // If we found a valid partition table, parse it to find partition offsets
                        if (valid)
                        {
                            // PS3 partition table format (big-endian):
                            // Offset 0x20: number of partitions (8 bytes)
                            // ... then partition entries at 0x28+
                            // Each entry: 8 bytes start sector, 8 bytes sector count
                            // But the format varies. Let's read more sectors and log them.
                            byte[] header = candidate.ReadSectors(0, 4);
                            Log($"    Decrypted sectors 0-3 (first 256 bytes):");
                            for (int row = 0; row < 256; row += 32)
                                Log($"      {row:X4}: {BitConverter.ToString(header, row, 32)}");

                            // Parse partition entries from the PS3 disk header
                            // Based on psdevwiki: the partition table starts at 0x20
                            // Format: 8-byte num_regions, then each region has:
                            //   8-byte start_sector, 8-byte sector_count, ...
                            long numPartitions = ReadBE64(header, 0x20);
                            Log($"    Number of partitions: {numPartitions}");

                            // Read partition entries — they follow the header
                            // The structure has entries at offsets 0x28 + (i * entrySize)
                            // Entry size appears to be 0x18 (24 bytes) or variable
                            // Let's try common entry layouts
                            List<long> partitionStarts = new();
                            for (int pi = 0; pi < Math.Min(numPartitions, 16); pi++)
                            {
                                // Try entry at 0x40 + (pi * 0xC0) based on known layouts
                                int entryBase = 0x40 + (pi * 0xC0);
                                if (entryBase + 16 > header.Length) break;

                                long pStart = ReadBE64(header, entryBase);
                                long pCount = ReadBE64(header, entryBase + 8);

                                if (pStart > 0 && pStart < _diskSource.SectorCount && pCount > 0)
                                {
                                    Log($"    Partition {pi}: start=0x{pStart:X} ({pStart}), count=0x{pCount:X} ({pCount})");
                                    partitionStarts.Add(pStart);
                                }
                            }

                            // Also try simpler layout: entries at 0x28 + (i * 0x10)
                            if (partitionStarts.Count == 0)
                            {
                                for (int pi = 0; pi < Math.Min(numPartitions, 16); pi++)
                                {
                                    int entryBase = 0x28 + (pi * 0x10);
                                    if (entryBase + 16 > header.Length) break;

                                    long pStart = ReadBE64(header, entryBase);
                                    long pCount = ReadBE64(header, entryBase + 8);

                                    if (pStart > 0 && pStart < _diskSource.SectorCount && pCount > 0)
                                    {
                                        Log($"    Partition {pi} (alt): start=0x{pStart:X} ({pStart}), count=0x{pCount:X} ({pCount})");
                                        partitionStarts.Add(pStart);
                                    }
                                }
                            }

                            // Scan all discovered partition starts for UFS2 superblock
                            // Find ALL UFS2 superblocks and pick the largest partition
                            var allStarts = new HashSet<long>(partitionStarts);
                            foreach (var cs in CandidatePartitionStarts) allStarts.Add(cs);

                            long bestPartStart = -1;
                            long bestCgCount = 0;

                            // Fine scan: every 8 sectors in first 2GB looking for UFS2
                            // The kpartx NOR example showed dev_hdd0 at sector 0x80010
                            // For NAND, it could be at a similar offset
                            Log($"    Fine-scanning first 2GB (every 8 sectors) for UFS2...");
                            int foundCount = 0;
                            for (long scanSec = 0; scanSec < 0x400000 && foundCount < 20; scanSec += 8)
                            {
                                long scanOff = (scanSec * 512) + 65536;
                                if (scanOff + 8192 > _diskSource.TotalSize) continue;
                                try
                                {
                                    byte[] scanData = candidate.ReadBytes(scanOff, 8192);
                                    uint scanMagic = (uint)((scanData[0x55C] << 24) | (scanData[0x55D] << 16) |
                                                             (scanData[0x55E] << 8) | scanData[0x55F]);
                                    if (scanMagic == 0x19540119)
                                    {
                                        int ncg = (scanData[0xBC] << 24) | (scanData[0xBD] << 16) |
                                                  (scanData[0xBE] << 8) | scanData[0xBF];
                                        Log($"    *** UFS2 at sector 0x{scanSec:X} ({ncg} CGs) ***");
                                        allStarts.Add(scanSec); // Add discovered offset to candidates
                                        foundCount++;
                                    }
                                }
                                catch { }
                            }
                            Log($"    Fine scan done. Found {foundCount} UFS2 superblock(s) in first 2GB.");

                            foreach (long ps in allStarts)
                            {
                                long sbOff = (ps * 512) + 65536;
                                if (sbOff + 8192 > _diskSource.TotalSize) continue;

                                try
                                {
                                    byte[] sbData = candidate.ReadBytes(sbOff, 8192);
                                    if (sbData.Length > 0x560)
                                    {
                                        uint sbMagic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                                               (sbData[0x55E] << 8) | sbData[0x55F]);

                                        if (sbMagic == 0x19540119)
                                        {
                                            // Read CG count to determine partition size
                                            int ncg = (sbData[0xBC] << 24) | (sbData[0xBD] << 16) |
                                                      (sbData[0xBE] << 8) | sbData[0xBF];
                                            Log($"    UFS2 found at sector 0x{ps:X}: {ncg} cylinder groups");

                                            if (ncg > bestCgCount)
                                            {
                                                bestCgCount = ncg;
                                                bestPartStart = ps;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (bestPartStart >= 0)
                            {
                                found = true;
                                foundMethod = label;
                                foundPartitionSector = bestPartStart;
                                foundBswap = useBswap;
                                foundCbc = true;
                                Log($"    Selected partition at sector 0x{bestPartStart:X} ({bestCgCount} CGs) as largest");
                            }

                            if (!found)
                            {
                                Log("    UFS2 superblock not found at any parsed partition offset.");
                                Log("    Doing broad sector scan...");
                                // Broad scan: check every 0x8000 sectors (every ~16MB)
                                for (long s = 0; s < Math.Min(_diskSource.SectorCount, 0x10000000L) && !found; s += 0x8000)
                                {
                                    long sbOff = (s * 512) + 65536;
                                    if (sbOff + 8192 > _diskSource.TotalSize) continue;

                                    try
                                    {
                                        byte[] sbData = candidate.ReadBytes(sbOff, 8192);
                                        if (sbData.Length > 0x560)
                                        {
                                            uint sbMagic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                                                   (sbData[0x55E] << 8) | sbData[0x55F]);
                                            if (sbMagic == 0x19540119)
                                            {
                                                found = true;
                                                foundMethod = label;
                                                foundPartitionSector = s;
                                                foundBswap = useBswap;
                                                foundCbc = true;
                                                Log($"    UFS2 found via broad scan at sector 0x{s:X}!");
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  [{label}] Sector 0 error: {ex.Message}");
                    }

                    if (!found)
                        candidate.Dispose();
                }

                // ─── Then try XTS-128 (NOR/Slim models) ───
                if (!skipXts)
                foreach (var (method, dataKey, tweakKey) in allKeys)
                {
                    if (found) break;

                    // Try with and without bswap16
                    foreach (bool useBswap in new[] { true, false })
                    {
                        if (found) break;

                        string label = $"{method} [bswap16={useBswap}]";
                        var candidate = new DecryptedDiskSource(
                            new NonDisposingDiskSource(_diskSource), dataKey, tweakKey, useBswap);

                    // First, check if sector 0 decrypts to a valid PS3 partition table
                    try
                    {
                        byte[] sector0 = candidate.ReadSectors(0, 1);
                        uint magic1 = (uint)((sector0[0x14] << 24) | (sector0[0x15] << 16) |
                                              (sector0[0x16] << 8) | sector0[0x17]);
                        uint magic2 = (uint)((sector0[0x1C] << 24) | (sector0[0x1D] << 16) |
                                              (sector0[0x1E] << 8) | sector0[0x1F]);

                        bool validPartTable = (magic1 == 0x0FACE0FF || magic2 == 0xDEADFACE);

                        if (validPartTable)
                        {
                            Log($"  [{label}] Sector 0 valid! (magic: {magic1:X8} / {magic2:X8})");
                        }
                        else
                        {
                            Log($"  [{label}] Sector 0 magic: {magic1:X8} / {magic2:X8} (expected 0FACE0FF/DEADFACE)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"  [{label}] Sector 0 read error: {ex.Message}");
                    }

                    // Scan candidate partition offsets for UFS2 superblock
                    foreach (long partStart in CandidatePartitionStarts)
                    {
                        if (found) break;

                        long sbByteOffset = (partStart * 512) + 65536;
                        if (sbByteOffset + 8192 > _diskSource.TotalSize) continue;

                        try
                        {
                            byte[] sbData = candidate.ReadBytes(sbByteOffset, 8192);

                            if (sbData.Length > 0x560)
                            {
                                uint magic = (uint)((sbData[0x55C] << 24) | (sbData[0x55D] << 16) |
                                                     (sbData[0x55E] << 8) | sbData[0x55F]);

                                if (magic == 0x19540119)
                                {
                                    found = true;
                                    foundMethod = label;
                                    foundPartitionSector = partStart;
                                    foundBswap = useBswap;

                                    _decryptedSource?.Dispose();
                                    _decryptedSource = new DecryptedDiskSource(
                                        _diskSource, dataKey, tweakKey, useBswap);
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"    [{label}] partition 0x{partStart:X}: error: {ex.Message}");
                        }
                    }

                    // Fine scan fallback: sweep first 2GB every 8 sectors
                    if (!found)
                    {
                        for (long scanSec = 0; scanSec < 0x400000 && !found; scanSec += 8)
                        {
                            long scanOff = (scanSec * 512) + 65536;
                            if (scanOff + 8192 > _diskSource.TotalSize) continue;
                            try
                            {
                                byte[] scanData = candidate.ReadBytes(scanOff, 8192);
                                uint scanMagic = (uint)((scanData[0x55C] << 24) | (scanData[0x55D] << 16) |
                                                         (scanData[0x55E] << 8) | scanData[0x55F]);
                                if (scanMagic == 0x19540119)
                                {
                                    found = true;
                                    foundMethod = label;
                                    foundPartitionSector = scanSec;
                                    foundBswap = useBswap;
                                    _decryptedSource?.Dispose();
                                    _decryptedSource = new DecryptedDiskSource(
                                        _diskSource, dataKey, tweakKey, useBswap);
                                    Log($"    [{label}] Fine scan found UFS2 at sector 0x{scanSec:X}");
                                }
                            }
                            catch { }
                        }
                    }

                    if (!found)
                    {
                        candidate.Dispose();
                    }
                    } // end bswap foreach
                }

                } // end if (!found) — full scan block
            });

            if (found)
            {
                Log($"SUCCESS: UFS2 superblock found!");
                Log($"  Key method: {foundMethod}");
                Log($"  Partition start: sector 0x{foundPartitionSector:X} ({foundPartitionSector})");

                // Create the appropriate decrypted source
                if (foundCbc)
                {
                    _decryptedSource?.Dispose();
                    var cbcSource = new DecryptedDiskSourceCbc(
                        _diskSource, cbcKeys.AtaDataKey, foundBswap);
                    _fileSystem = new Ufs2FileSystem(cbcSource, foundPartitionSector);

                    // Store for reopening with write access
                    _cbcKey = (byte[])cbcKeys.AtaDataKey.Clone();
                    _cbcBswap = foundBswap;
                    _partitionSector = foundPartitionSector;
                    _isXts = false;
                }
                else
                {
                    // XTS path — _decryptedSource already set during scan
                    _fileSystem = new Ufs2FileSystem(_decryptedSource!, foundPartitionSector);
                    _partitionSector = foundPartitionSector;
                    _xtsBswap = foundBswap;
                    _isXts = true;

                    // Find and store the XTS keys from the successful method
                    foreach (var (method, dk, tk) in allKeys)
                    {
                        if (foundMethod.Contains(method))
                        {
                            _xtsDataKey = (byte[])dk.Clone();
                            _xtsTweakKey = (byte[])tk.Clone();
                            break;
                        }
                    }
                }

                IsDecrypted = true;
                EncryptionHint = ""; // Clear hint after use

                // Build the disk layout with the discovered partition
                _diskLayout = new Ps3DiskLayout
                {
                    Partitions = new List<Ps3Partition>()
                };

                if (foundPartitionSector > 0)
                {
                    _diskLayout.Partitions.Add(new Ps3Partition
                    {
                        Index = 0,
                        Name = "System",
                        StartSector = 0,
                        SectorCount = foundPartitionSector,
                        Type = Ps3PartitionType.System
                    });
                }

                _diskLayout.Partitions.Add(new Ps3Partition
                {
                    Index = _diskLayout.Partitions.Count,
                    Name = "GameOS (UFS2)",
                    StartSector = foundPartitionSector,
                    SectorCount = _diskSource.SectorCount - foundPartitionSector,
                    Type = Ps3PartitionType.GameOS
                });

                _diskLayout.DataRegionStartSector = foundPartitionSector;
                _diskLayout.DataRegionSectorCount = _diskSource.SectorCount - foundPartitionSector;

                Partitions.Clear();
                foreach (var p in _diskLayout.Partitions)
                    Partitions.Add(p);

                if (DiskInfo != null)
                {
                    DiskInfo.IsDecrypted = true;
                    DiskInfo.PartitionCount = Partitions.Count;
                    DiskInfo.Status = "Decrypted — mounting UFS2 filesystem...";
                }

                StatusText = "Decryption successful. Mounting filesystem...";

                // ─── AUTO-SAVE: cache the successful key combo for instant re-decrypt ───
                try
                {
                    var profile = new DriveProfile
                    {
                        Fingerprint = driveFingerprint,
                        EncryptionType = foundCbc ? "CBC-192" : "XTS-128",
                        Bswap16 = foundBswap,
                        PartitionSector = foundPartitionSector,
                        Label = foundMethod,
                        DriveSizeBytes = _diskSource.TotalSize
                    };

                    if (foundCbc && _cbcKey != null)
                    {
                        profile.DataKeyHex = Convert.ToHexString(_cbcKey);
                    }
                    else if (_xtsDataKey != null && _xtsTweakKey != null)
                    {
                        profile.DataKeyHex = Convert.ToHexString(_xtsDataKey);
                        profile.TweakKeyHex = Convert.ToHexString(_xtsTweakKey);
                    }

                    _driveProfiles.Save(profile);
                    Log($"Drive profile cached for instant re-decrypt (fingerprint: {driveFingerprint[..16]}...)");
                }
                catch (Exception ex)
                {
                    Log($"Warning: could not cache drive profile: {ex.Message}");
                }

                await MountFilesystemAsync();
            }
            else
            {
                LogSeparator();
                Log("FAILED: No valid UFS2 superblock found with any key/offset/bswap combination.");
                Log("");

                // Dump CBC-192 decrypted sector 0 (most likely for CECHA)
                foreach (bool bswap in new[] { false, true })
                {
                    try
                    {
                        string lbl = $"AES-CBC-192 [bswap16={bswap}]";
                        using var cbcDebug = new DecryptedDiskSourceCbc(
                            new NonDisposingDiskSource(_diskSource), cbcKeys.AtaDataKey, bswap);
                        byte[] s0 = cbcDebug.ReadSectors(0, 1);
                        Log($"[{lbl}] Decrypted sector 0 (first 64 bytes):");
                        Log($"  {BitConverter.ToString(s0, 0, 64)}");
                        Log("");
                    }
                    catch (Exception ex)
                    {
                        Log($"[CBC-192 bswap={bswap}] Error: {ex.Message}");
                    }
                }

                // Also dump first XTS key for comparison
                var (m, dk, tk) = allKeys[0];
                foreach (bool bswap in new[] { true, false })
                {
                    try
                    {
                        string lbl = $"{m} [bswap16={bswap}]";
                        using var debugSource = new DecryptedDiskSource(
                            new NonDisposingDiskSource(_diskSource), dk, tk, bswap);
                        byte[] s0 = debugSource.ReadSectors(0, 1);
                        Log($"[{lbl}] Decrypted sector 0 (first 64 bytes):");
                        Log($"  {BitConverter.ToString(s0, 0, 64)}");
                        Log("");
                    }
                    catch (Exception ex)
                    {
                        Log($"[{m} bswap={bswap}] Error: {ex.Message}");
                    }
                }

                Log($"Log file saved to: {LogFilePath}");
                LogSeparator();

                IsDecrypted = true;
                StatusText = $"No UFS2 found. Log saved to Desktop (PS3HddTool_log.txt).";

                if (DiskInfo != null)
                {
                    DiskInfo.IsDecrypted = true;
                    DiskInfo.Status = "Decrypted but UFS2 not found — see log on Desktop.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Decryption error: {ex.Message}";
            Log($"ERROR during decryption: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// Mount the UFS2 filesystem from the GameOS partition.
    /// Falls back to recovery mode if standard mount fails.
    /// </summary>
    private async Task MountFilesystemAsync()
    {
        // If _fileSystem was already created (CBC path), try to mount it with recovery fallback
        if (_fileSystem != null && _diskLayout != null)
        {
            try
            {
                bool mounted = false;
                await Task.Run(() => { mounted = _fileSystem.Mount(); });

                if (!mounted)
                {
                    StatusText = "UFS2 mount failed. Attempting recovery mode...";
                    Log("ERROR: UFS2 mount failed during detailed superblock parse. Attempting recovery...");
                    await TryRecoveryMountAsync();
                    return;
                }

                IsFilesystemMounted = true;
                IsRecoveryMode = false;
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
                StatusText = $"Mount error: {ex.Message}. Attempting recovery...";
                Log($"ERROR mounting filesystem: {ex.Message}");
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
                StatusText = "UFS2 mount failed. Attempting recovery mode...";
                Log("ERROR: UFS2 mount failed during detailed superblock parse. Attempting recovery...");
                await TryRecoveryMountAsync();
                return;
            }

            IsFilesystemMounted = true;
            IsRecoveryMode = false;
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
            StatusText = $"Mount error: {ex.Message}. Attempting recovery...";
            Log($"ERROR mounting filesystem: {ex.Message}");
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
            StatusText = "RECOVERY MODE: Scanning for displaced superblocks...";
            LogSeparator();
            Log("═══════════════════════════════════════════════════════");
            Log("RECOVERY MODE ACTIVATED - SCANNING FOR CORRUPTED FILESYSTEM");
            Log("═══════════════════════════════════════════════════════");

            _recoveryModeHelper = new RecoveryModeHelper(_diskSource, _partitionSector, msg => Log(msg));

            var (recSuccess, recFs, recErrorMsg) = await _recoveryModeHelper.TryMountFilesystemAsync();

            if (!recSuccess || recFs == null)
            {
                StatusText = $"Recovery mode failed: {recErrorMsg}";
                Log($"RECOVERY FAILED: {recErrorMsg}");
                return;
            }

            _recoveryFileSystem = recFs;

            Log($"Recovery filesystem created with superblock configuration:");
            Log($"  BlockSize: {recFs.Superblock.BlockSize}");
            Log($"  FragmentSize: {recFs.Superblock.FragmentSize}");
            Log($"  CylinderGroups: {recFs.Superblock.CylinderGroups}");
            Log($"  InodesPerGroup: {recFs.Superblock.InodesPerGroup}");

            // Build file tree from recovery filesystem
            StatusText = "Recovery mode: loading file tree...";
            var recoveryTree = await _recoveryModeHelper.BuildRecoveryFileTree(recFs);

            // Perform deep scan to report recovery stats
            var (readable, corrupted, recoverable) = await _recoveryModeHelper.PerformDeepScanAsync(recFs);
            
            Log($"");
            Log($"═══════════════════════════════════════════════════════");
            Log($"RECOVERY STATISTICS:");
            Log($"  Readable files: {readable}");
            Log($"  Corrupted/Unreadable files: {corrupted}");
            Log($"  Recoverable bytes: {FormatSize(recoverable)}");
            if (readable + corrupted > 0)
                Log($"  Recovery rate: {(double)readable / (readable + corrupted) * 100:F1}%");
            Log($"═══════════════════════════════════════════════════════");
            Log($"");

            // Update UI with recovery results
            IsFilesystemMounted = true;
            IsRecoveryMode = true;
            FileTree.Clear();
            foreach (var node in recoveryTree.OrderByDescending(n => n.IsDirectory).ThenBy(n => n.Name))
                FileTree.Add(node);

            if (DiskInfo != null)
            {
                DiskInfo.HasValidUfs2 = true;
                DiskInfo.Status = $"🔧 RECOVERY MODE: {readable}/{readable + corrupted} files readable ({(double)readable / (readable + corrupted) * 100:F0}%)";
            }

            StatusText = $"Recovery mode active — {readable} readable, {corrupted} corrupted";
            Log($"Recovery mode: loaded {FileTree.Count} directory entries");
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

    // [Rest of the file continues - keeping all other methods unchanged...]
    
    public async Task ExpandNodeAsync(FileTreeNode node)
    {
        if (!node.IsDirectory || node.ChildrenLoaded || _fileSystem == null) return;

        try
        {
            List<FileTreeNode> children = new();

            await Task.Run(() =>
            {
                var inode = _fileSystem.ReadInode(node.InodeNumber);
                var entries = _fileSystem.ReadDirectory(inode);

                foreach (var entry in entries)
                {
                    if (entry.Name == "." || entry.Name == "..") continue;
                    var childInode = _fileSystem.ReadInode(entry.InodeNumber);
                    var childNode = FileTreeNode.FromInode(childInode, entry.Name, node.FullPath, node.InodeNumber);
                    children.Add(childNode);
                }
            });

            node.Children.Clear();
            foreach (var child in children.OrderByDescending(c => c.IsDirectory).ThenBy(c => c.Name))
                node.Children.Add(child);

            node.ChildrenLoaded = true;
        }
        catch (Exception ex)
        {
            Log($"Error expanding {node.FullPath}: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ExtractAsync((FileTreeNode Node, string OutputPath) args)
    {
        if (_fileSystem == null) return;

        try
        {
            IsBusy = true;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            var node = args.Node;
            string outputPath = args.OutputPath;

            Log($"Extracting {node.FullPath} to {outputPath}...");
            StatusText = $"Extracting {node.Name}...";

            await Task.Run(() =>
            {
                var inode = _fileSystem.ReadInode(node.InodeNumber);

                if (node.IsDirectory)
                {
                    IsProgressIndeterminate = true;
                    var progress = new Progress<string>(p =>
                        ProgressText = $"Extracting: {Path.GetFileName(p)}");

                    _fileSystem.ExtractDirectory(inode, outputPath, progress);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    long totalSize = inode.Size;
                    var startTime = DateTime.UtcNow;
                    long lastUpdate = 0;

                    _fileSystem.ExtractInodeToStream(inode, fs, bytesWritten =>
                    {
                        if (bytesWritten - lastUpdate < 512 * 1024 && bytesWritten < totalSize) return;
                        lastUpdate = bytesWritten;

                        double pct = totalSize > 0 ? (double)bytesWritten / totalSize * 100 : 0;
                        ProgressValue = pct;

                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        double speedMBps = elapsed > 0.1 ? (bytesWritten / (1024.0 * 1024.0)) / elapsed : 0;

                        string eta = "";
                        if (speedMBps > 0.01 && bytesWritten < totalSize)
                        {
                            double remainMB = (totalSize - bytesWritten) / (1024.0 * 1024.0);
                            int etaSec = (int)(remainMB / speedMBps);
                            eta = etaSec >= 60 ? $" — ETA {etaSec / 60}m {etaSec % 60}s" : $" — ETA {etaSec}s";
                        }

                        ProgressText = $"{pct:F1}%  {bytesWritten / (1024 * 1024)}/{totalSize / (1024 * 1024)} MB  {speedMBps:F1} MB/s{eta}";
                    });
                }
            });

            ProgressValue = 100;
            StatusText = $"Extraction complete: {node.Name}";
            Log($"Extraction complete: {outputPath}");
        }
        catch (Exception ex)
        {
            StatusText = $"Extraction error: {ex.Message}";
            Log($"ERROR extracting: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            ProgressValue = 0;
            IsProgressIndeterminate = false;
        }
    }

    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "PS3HddTool_log.txt");

    public void Log(string message)
    {
        string timestamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        LogMessages.Add(timestamped);

        try
        {
            File.AppendAllText(LogFilePath, timestamped + Environment.NewLine);
        }
        catch { }
    }

    private void LogSeparator()
    {
        Log(new string('=', 70));
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F2} {units[i]}";
    }

    public void RefreshFreeSpace()
    {
        if (_fileSystem?.Superblock == null) return;
        try
        {
            _fileSystem.Mount();
            var sb = _fileSystem.Superblock!;
            FreeSpaceText = $"Free: {FormatSize(sb.FreeSpaceBytes)}";
        }
        catch { }
    }

    public void DeselectNode()
    {
        SelectedNode = null;
        HasImagePreview = false;
        ImagePreview = null;
    }

    private static long ReadBE64(byte[] data, int offset)
    {
        return ((long)data[offset] << 56) | ((long)data[offset + 1] << 48) |
               ((long)data[offset + 2] << 40) | ((long)data[offset + 3] << 32) |
               ((long)data[offset + 4] << 24) | ((long)data[offset + 5] << 16) |
               ((long)data[offset + 6] << 8) | data[offset + 7];
    }

    [ObservableProperty] private bool _dryRunMode = true;
    [ObservableProperty] private bool _verboseDiagnostics = false;

    public void Cleanup()
    {
        _fileSystem = null;
        _decryptedSource?.Dispose();
        _diskSource?.Dispose();
    }
}
