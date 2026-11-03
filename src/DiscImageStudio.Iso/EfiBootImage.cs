using System.Buffers.Binary;

namespace DiscImageStudio.Iso;

/// <summary>Checks the BPB of a raw FAT volume, without interpreting its boot program.</summary>
internal static class EfiBootImage
{
    internal static void Validate(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists) throw new FileNotFoundException("EFI boot image does not exist.", path);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported for EFI boot images: {path}");
        if (file.Length < 512 || file.Length % 512 != 0 || file.Length > uint.MaxValue)
            throw new ArgumentException("EFI boot image must be a raw FAT volume, a multiple of 512 bytes and smaller than 4 GiB.");

        using FileStream input = file.OpenRead();
        Span<byte> boot = stackalloc byte[512];
        input.ReadExactly(boot);
        uint bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        uint clusterSectors = boot[13];
        uint reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        uint fats = boot[16];
        uint rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
        uint sectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
        if (sectors == 0) sectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[32..]);
        uint fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
        bool fat32 = fatSectors == 0;
        if (fat32) fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
        if (boot[510] != 0x55 || boot[511] != 0xAA
            || bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || clusterSectors is 0 or > 128 || (clusterSectors & (clusterSectors - 1)) != 0
            || reserved == 0 || fats is 0 or > 2 || fatSectors == 0
            || sectors == 0 || (ulong)sectors * bytesPerSector != (ulong)file.Length)
            throw new ArgumentException("EFI boot image has an invalid FAT BPB or volume length. Supply a raw FAT volume, not a partitioned disk image.");

        ulong overhead = reserved + (ulong)fats * fatSectors
            + ((ulong)rootEntries * 32 + bytesPerSector - 1) / bytesPerSector;
        ulong clusters = sectors > overhead ? (sectors - overhead) / clusterSectors : 0;
        int bits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
        ulong fatBytes = ((clusters + 2) * (uint)bits + 7) / 8;
        if (clusters == 0 || clusters >= 0x0FFFFFF5 || fat32 != (bits == 32)
            || fatBytes > (ulong)fatSectors * bytesPerSector
            || (!fat32 && rootEntries == 0)
            || (fat32 && (rootEntries != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(boot[42..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]) < 2
                || BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]) >= clusters + 2)))
            throw new ArgumentException("EFI boot image has inconsistent FAT geometry.");
    }
}
