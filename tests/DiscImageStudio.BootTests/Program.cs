using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Iso;
using DvdImageSolver;

string directory = Path.Combine(Path.GetTempPath(), "disc-boot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    BootTests.Run(directory);
}
finally
{
    Directory.Delete(directory, recursive: true);
}

internal static class BootTests
{
    private const int SectorBytes = 2048;
    private const uint DiscSectors = 1024;
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;

    internal static void Run(string directory)
    {
        CheckLegacyLayout(directory);
        string efi = Path.Combine(directory, "efi image.img");
        byte[] fat = Fat12();
        File.WriteAllBytes(efi, fat);
        string data = Path.Combine(directory, "files");
        Directory.CreateDirectory(Path.Combine(data, "日本語"));
        byte[] payload = Enumerable.Range(0, 4001).Select(i => (byte)(i * 37)).ToArray();
        File.WriteAllBytes(Path.Combine(data, "日本語", "hello.bin"), payload);
        File.WriteAllBytes(Path.Combine(data, "empty"), []);

        foreach (bool outer in new[] { false, true })
        {
            var legacy = Iso9660ImageWriter.Plan(data, DiscSectors, "BOOT", Timestamp, outer);
            var explicitOff = Iso9660ImageWriter.Plan(data, DiscSectors, "BOOT", Timestamp, outer, null);
            Require(Render(legacy).SequenceEqual(Render(explicitOff)), "omitted boot option preserves output");
            Require(legacy.BootCatalogLba is null && legacy.EfiBootImageLba is null, "no boot reservation by default");
            byte[] old = Render(legacy);
            Require(old[17 * SectorBytes] == 2 && old[18 * SectorBytes] == 255, "legacy descriptor locations");

            var plan = Iso9660ImageWriter.Plan(data, DiscSectors, "BOOT", Timestamp, outer, efi);
            byte[] bytes = Render(plan);
            CheckBoot(bytes, fat, outer ? plan.MetadataEndLbaExclusive : plan.DataEndLbaExclusive);
            foreach (int descriptor in new[] { 16, 18 })
                CheckDirectories(bytes, descriptor, payload,
                    outer ? plan.MetadataEndLbaExclusive : plan.DataEndLbaExclusive,
                    outer ? plan.FileDataStartLba : DiscSectors);
            var bootOnly = Iso9660ImageWriter.Plan(null, DiscSectors, "BOOT", Timestamp, outer, efi);
            CheckBoot(Render(bootOnly), fat, bootOnly.MetadataEndLbaExclusive);
            Require(bootOnly.FileCount == 0, "boot image does not become an ordinary file");
            CheckGeneration(directory, data, efi, fat, outer);
        }

        CheckGuards(directory, data, efi, fat);
        CheckFatVariants(directory);
        foreach (var module in new IOpticalDiscModule[] { new CdDiscModule(), new DvdDiscModule() })
            Require(module.Descriptor.Commands.First().Options.Any(o => o.Name == "efi-boot-image" && !o.Required),
                "boot option registered in command metadata");
        Console.WriteLine("UEFI boot: passed (descriptors, catalog checksum, FAT12/16/32, large image, extents, streaming, CLI, guards and defaults)");
    }

    private static void CheckLegacyLayout(string directory)
    {
        string data = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(Path.Combine(data, "資料"));
        File.WriteAllBytes(Path.Combine(data, "test.bin"), Enumerable.Range(0, 5100).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(data, "資料", "中文.txt"), "Unicode file");
        foreach (string folder in new[] { data, Path.Combine(data, "資料") })
            Directory.SetLastWriteTimeUtc(folder, DateTime.UnixEpoch);
        foreach (string file in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch);
        // SHA-256 fixtures generated with the ISO writer before El Torito support.
        foreach ((string? source, bool outer, string hash) in new (string?, bool, string)[]
        {
            (null, false, "6202FA9FF00A7234521870ADDA6BE83493F8ADDE40E44E68A071F86906D2A717"),
            (null, true, "6202FA9FF00A7234521870ADDA6BE83493F8ADDE40E44E68A071F86906D2A717"),
            (data, false, "94859B4293C1581C78D00265CB3B6A3F54A2B12529C4D28CCD09137D66EE148E"),
            (data, true, "5E3935324EC6627C8F284C9FCA29D2604EA2D9E3C0D5A94190AF513893255172"),
        })
        {
            var plan = Iso9660ImageWriter.Plan(source, 256, "LEGACY", Timestamp, outer);
            using MemoryStream bytes = new();
            Iso9660ImageWriter.WriteSequentialPrefix(plan, bytes, 256);
            Require(Convert.ToHexString(SHA256.HashData(bytes.ToArray())) == hash,
                "boot-disabled output matches pre-El-Torito fixture");
        }
    }

    private static byte[] Render(Iso9660ImageWriter.LayoutPlan plan)
    {
        using MemoryStream memory = new();
        using ForwardOnlyStream output = new(memory);
        if (plan.FilesAtOuterRing)
        {
            Iso9660ImageWriter.WriteOuterRingPrefix(plan, output, plan.MetadataEndLbaExclusive);
            byte[] drawing = new byte[checked((int)(plan.FileDataStartLba - plan.MetadataEndLbaExclusive) * SectorBytes)];
            Array.Fill(drawing, (byte)0xA7);
            output.Write(drawing);
            Iso9660ImageWriter.WriteOuterRingSuffix(plan, output, plan.FileDataStartLba);
        }
        else
        {
            Iso9660ImageWriter.WriteSequentialPrefix(plan, output, plan.DataEndLbaExclusive);
            byte[] drawing = new byte[checked((int)(plan.VolumeSectors - plan.DataEndLbaExclusive) * SectorBytes)];
            Array.Fill(drawing, (byte)0xA7);
            output.Write(drawing);
        }
        Require(memory.Length == (long)plan.VolumeSectors * SectorBytes, "full volume length on a forward-only stream");
        return memory.ToArray();
    }

    // Decode on-disc bytes without using the production catalog/layout serializers.
    private static void CheckBoot(byte[] bytes, byte[] fat, uint drawingStart)
    {
        Require(bytes.AsSpan(0, 16 * SectorBytes).IndexOfAnyExcept((byte)0) < 0, "system area stays zero");
        int[] types = [1, 0, 2, 255];
        for (int i = 0; i < types.Length; i++)
        {
            int descriptor = (16 + i) * SectorBytes;
            Require(bytes[descriptor] == types[i] && bytes[descriptor + 6] == 1
                && bytes.AsSpan(descriptor + 1, 5).SequenceEqual("CD001"u8), "descriptor sequence and version");
        }
        Require(bytes.AsSpan(17 * SectorBytes + 7, 32).SequenceEqual(
            Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION".PadRight(32, '\0'))), "boot system identifier with zero padding");
        Require(bytes.AsSpan(17 * SectorBytes + 39, 32).IndexOfAnyExcept((byte)0) < 0, "unused boot identifier");
        uint catalogLba = U32(bytes, 17 * SectorBytes + 71);
        Require(catalogLba == 20, "catalog sector follows descriptors");
        int catalog = checked((int)catalogLba * SectorBytes);
        Require(bytes[catalog] == 1 && bytes[catalog + 1] == 0xEF
            && bytes[catalog + 30] == 0x55 && bytes[catalog + 31] == 0xAA, "EFI validation entry and keys");
        int sum = 0;
        for (int i = 0; i < 32; i += 2) sum += U16(bytes, catalog + i);
        Require((sum & 65535) == 0, "validation entry 16-bit checksum");
        Require(bytes[catalog + 32] == 0x88 && bytes[catalog + 33] == 0
            && U16(bytes, catalog + 34) == 0 && U16(bytes, catalog + 36) == 0, "bootable no-emulation entry");
        int count = fat.Length / 512;
        Require(U16(bytes, catalog + 38) == (count > 65535 ? 0 : count), "load count uses 512-byte sectors");
        uint bootLba = U32(bytes, catalog + 40);
        Require(bootLba > catalogLba && bootLba + (fat.Length + 2047u) / SectorBytes <= drawingStart,
            "entire FAT image is reserved before drawing");
        Require(bytes.AsSpan(checked((int)bootLba * SectorBytes), fat.Length).SequenceEqual(fat), "FAT image copied byte for byte");
        int padding = (SectorBytes - fat.Length % SectorBytes) % SectorBytes;
        Require(bytes.AsSpan(checked((int)bootLba * SectorBytes + fat.Length), padding).IndexOfAnyExcept((byte)0) < 0,
            "partial ISO sector is zero padded");
        Require(bytes.AsSpan(catalog + 44, SectorBytes - 44).IndexOfAnyExcept((byte)0) < 0, "unused catalog entries stay zero");
    }

    private static void CheckDirectories(byte[] bytes, int descriptor, byte[] payload, uint drawingStart, uint drawingEnd)
    {
        int found = 0;
        ReadDirectory(descriptor * SectorBytes + 156);
        Require(found == 2, "nested and empty files remain readable in both filesystems");

        void ReadDirectory(int record)
        {
            uint lba = Both(record + 2), size = Both(record + 10);
            Require(lba + (size + 2047) / SectorBytes <= drawingStart, "directory stays before drawing");
            int end = checked((int)(lba * SectorBytes + size));
            for (int offset = checked((int)lba * SectorBytes); offset < end;)
            {
                int length = bytes[offset];
                if (length == 0) { offset = (offset / SectorBytes + 1) * SectorBytes; continue; }
                Require(length >= 34 && offset + length <= end, "bounded directory entry");
                if (bytes[offset + 32] > 1)
                {
                    if ((bytes[offset + 25] & 2) != 0) ReadDirectory(offset);
                    else
                    {
                        uint fileLba = Both(offset + 2), fileBytes = Both(offset + 10);
                        if (fileBytes > 0)
                        {
                            Require(fileLba >= drawingEnd || fileLba + (fileBytes + 2047) / SectorBytes <= drawingStart,
                                "file extent excludes drawing");
                            Require(bytes.AsSpan(checked((int)fileLba * SectorBytes), checked((int)fileBytes)).SequenceEqual(payload),
                                "ordinary file bytes preserved");
                        }
                        found++;
                    }
                }
                offset += length;
            }
        }

        uint Both(int offset)
        {
            uint value = U32(bytes, offset);
            Require(value == BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4)), "ISO dual-endian field");
            return value;
        }
    }

    private static void CheckGeneration(string directory, string data, string efi, byte[] fat, bool outer)
    {
        string image = Path.Combine(AppContext.BaseDirectory, "input.png");
        CdDiscParameters cdOptions = new(24.5, 56.8, DiscSectors);
        CdDataLayoutPlan cd = CdDataImageGenerator.PrepareLayout(data, cdOptions, "BOOT", outer, efi);
        using MemoryStream cdBytes = new();
        using (ForwardOnlyStream forward = new(cdBytes))
            CdDataImageGenerator.GenerateToStream(image, forward, cdOptions, cd);
        CheckBoot(cdBytes.ToArray(), fat, cd.DrawingStartLba);
        CdDataLayoutPlan cdLegacy = CdDataImageGenerator.PrepareLayout(data, cdOptions, "BOOT", outer);
        using MemoryStream cdLegacyBytes = new();
        CdDataImageGenerator.GenerateToStream(image, cdLegacyBytes, cdOptions, cdLegacy);
        CheckDrawing(cdBytes.ToArray(), cdLegacyBytes.ToArray(), cd.DrawingStartLba,
            Math.Min(cd.DrawingEndLbaExclusive, cdLegacy.DrawingEndLbaExclusive));
        CheckDirectories(cdBytes.ToArray(), 18, File.ReadAllBytes(Path.Combine(data, "日本語", "hello.bin")), cd.DrawingStartLba, cd.DrawingEndLbaExclusive);
        DvdStreamingOptions dvdOptions = new(DiscSectors, 24, 58, FastParallelism: 1);
        DvdHybridStreamingPlan dvd = DvdStreamingGenerator.PrepareHybrid(data, dvdOptions, "BOOT", outer, efi);
        Require(dvd.DrawingStartLba % 16 == 0 && dvd.DrawingEndLbaExclusive % 16 == 0 && dvd.DrawingSectors >= 16,
            "DVD drawing stays ECC block aligned");
        using MemoryStream dvdBytes = new();
        using (ForwardOnlyStream forward = new(dvdBytes))
            DvdStreamingGenerator.GenerateHybrid(image, forward, dvdOptions, dvd);
        CheckBoot(dvdBytes.ToArray(), fat, dvd.DrawingStartLba);
        DvdHybridStreamingPlan dvdLegacy = DvdStreamingGenerator.PrepareHybrid(data, dvdOptions, "BOOT", outer);
        using MemoryStream dvdLegacyBytes = new();
        DvdStreamingGenerator.GenerateHybrid(image, dvdLegacyBytes, dvdOptions, dvdLegacy);
        CheckDrawing(dvdBytes.ToArray(), dvdLegacyBytes.ToArray(), dvd.DrawingStartLba,
            Math.Min(dvd.DrawingEndLbaExclusive, dvdLegacy.DrawingEndLbaExclusive));
        CheckDirectories(dvdBytes.ToArray(), 18, File.ReadAllBytes(Path.Combine(data, "日本語", "hello.bin")), dvd.DrawingStartLba, dvd.DrawingEndLbaExclusive);
        string output = Path.Combine(directory, "cli.iso");
        var result = new CdDiscModule().ExecuteAsync(new("cd-generate",
            ["--input", image, "--output", output, "--sectors", "1024", "--data-dir", data,
             "--volume-label", "BOOT", "--file-position", outer ? "outer" : "inner", "--efi-boot-image", efi])).GetAwaiter().GetResult();
        Require(result.Succeeded, "CD CLI boot option");
        NormalizeDates(cdBytes.GetBuffer());
        byte[] cli = File.ReadAllBytes(output);
        NormalizeDates(cli);
        Require(cli.SequenceEqual(cdBytes.ToArray()), "CD CLI and streaming parity");
        Require(DvdCommandRunner.Run(["solve", "--image", image, "--iso-output", output,
            "--total-sectors", "1024", "--inner-radius-mm", "24", "--outer-radius-mm", "58", "--data-dir", data,
            "--volume-label", "BOOT", "--file-position", outer ? "outer" : "inner", "--efi-boot-image", efi, "--fast-parallelism", "1"]) == 0, "DVD CLI boot option");
        NormalizeDates(dvdBytes.GetBuffer());
        cli = File.ReadAllBytes(output);
        NormalizeDates(cli);
        Require(cli.SequenceEqual(dvdBytes.ToArray()), "DVD CLI and streaming parity");
        // No data directory is required, for either family.
        Require(new CdDiscModule().ExecuteAsync(new("cd-generate",
            ["--input", image, "--output", output, "--sectors", "1024", "--efi-boot-image", efi]))
            .GetAwaiter().GetResult().Succeeded, "CD boot-only CLI");
        Require(DvdCommandRunner.Run(["solve", "--image", image, "--iso-output", output,
            "--total-sectors", "1024", "--efi-boot-image", efi, "--fast-parallelism", "1"]) == 0, "DVD boot-only CLI");
        CheckBoot(File.ReadAllBytes(output), fat, 768);
    }

    private static void CheckDrawing(byte[] boot, byte[] legacy, uint start, uint end)
    {
        int offset = checked((int)start * SectorBytes);
        int length = checked((int)(end - start) * SectorBytes);
        Require(length > 0 && boot.AsSpan(offset, length).SequenceEqual(legacy.AsSpan(offset, length)),
            "boot reservation preserves drawing at the same full-disc LBA");
    }

    private static void CheckGuards(string directory, string data, string efi, byte[] fat)
    {
        Throws<ArgumentException>(() => Iso9660ImageWriter.Plan(data, 720, "FULL", efiBootImagePath: efi));
        Throws<ArgumentException>(() => CdDataImageGenerator.PrepareLayout(data, new(24.5, 56.8, 751), efiBootImagePath: efi));
        Throws<ArgumentException>(() => DvdStreamingGenerator.PrepareHybrid(data, new(752, 24, 58), efiBootImagePath: efi));
        var plan = Iso9660ImageWriter.Plan(data, DiscSectors, "BOOT", efiBootImagePath: efi);
        File.WriteAllBytes(efi, fat[..^512]);
        using MemoryStream output = new();
        Throws<IOException>(() => Iso9660ImageWriter.WriteSequentialPrefix(plan, output, DiscSectors));
        Require(output.Length == 0, "changed boot source rejected before prefix");
        File.WriteAllBytes(efi, fat);
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Throws<OperationCanceledException>(() => Iso9660ImageWriter.WriteSequentialPrefix(plan, output, DiscSectors, cancel.Token));
        Require(output.Length == 0, "cancellation before prefix");
        string bad = Path.Combine(directory, "bad.img");
        foreach (int size in new[] { 0, 511, 513, 1024 })
        {
            File.WriteAllBytes(bad, new byte[size]);
            Throws<ArgumentException>(() => Iso9660ImageWriter.Plan(null, DiscSectors, "BAD", efiBootImagePath: bad));
        }
        foreach (int offset in new[] { 11, 13, 14, 16, 19, 22, 510 })
        {
            byte[] invalid = (byte[])fat.Clone();
            invalid[offset] = 0;
            if (offset == 11) invalid[12] = 0;
            File.WriteAllBytes(bad, invalid);
            Throws<ArgumentException>(() => Iso9660ImageWriter.Plan(null, DiscSectors, "BAD", efiBootImagePath: bad));
        }
        Throws<FileNotFoundException>(() => Iso9660ImageWriter.Plan(null, DiscSectors, "BAD", efiBootImagePath: bad + "missing"));
        string sentinel = Path.Combine(directory, "preserved.iso");
        File.WriteAllText(sentinel, "keep");
        Require(DvdCommandRunner.Run(["solve", "--image", Path.Combine(AppContext.BaseDirectory, "input.png"),
            "--iso-output", sentinel, "--total-sectors", "736", "--efi-boot-image", efi]) != 0, "capacity rejected through CLI");
        Require(File.ReadAllText(sentinel) == "keep", "failed plan preserves output");
        Require(DvdCommandRunner.Run(["solve", "--image", Path.Combine(AppContext.BaseDirectory, "input.png"),
            "--iso-output", efi, "--total-sectors", "1024", "--efi-boot-image", efi]) != 0, "cannot overwrite boot source");
        Require(File.ReadAllBytes(efi).SequenceEqual(fat), "boot source preserved");
    }

    private static void CheckFatVariants(string directory)
    {
        foreach (bool fat32 in new[] { false, true })
        {
            // FAT16: 8192 sectors. FAT32: 131072 sectors (> 16-bit catalog count).
            int sectors = fat32 ? 131072 : 8192;
            byte[] fat = new byte[sectors * 512];
            fat[0] = 0xEB; fat[2] = 0x90;
            Put16(fat, 11, 512); fat[13] = 1; Put16(fat, 14, fat32 ? 32 : 1); fat[16] = 2;
            Put16(fat, 17, fat32 ? 0 : 512);
            if (fat32) { Put32(fat, 32, sectors); Put32(fat, 36, 1024); Put32(fat, 44, 2); }
            else { Put16(fat, 19, sectors); Put16(fat, 22, 32); }
            fat[510] = 0x55; fat[511] = 0xAA;
            string path = Path.Combine(directory, "variant.img");
            File.WriteAllBytes(path, fat);
            uint total = checked((uint)(fat.Length / SectorBytes + 64));
            var plan = Iso9660ImageWriter.Plan(null, total, "FAT", Timestamp, efiBootImagePath: path);
            CheckBoot(Render(plan), fat, plan.MetadataEndLbaExclusive);
        }
        // An odd number of 512-byte sectors must be rounded up only in ISO placement.
        byte[] partial = Fat12()[..^512];
        Put16(partial, 19, 2879);
        string partialPath = Path.Combine(directory, "partial.img");
        File.WriteAllBytes(partialPath, partial);
        var partialPlan = Iso9660ImageWriter.Plan(null, DiscSectors, "PARTIAL", Timestamp, efiBootImagePath: partialPath);
        CheckBoot(Render(partialPlan), partial, partialPlan.MetadataEndLbaExclusive);
    }

    private static byte[] Fat12()
    {
        byte[] fat = new byte[2880 * 512];
        fat[0] = 0xEB; fat[1] = 0x3C; fat[2] = 0x90;
        Put16(fat, 11, 512); fat[13] = 1; Put16(fat, 14, 1); fat[16] = 2;
        Put16(fat, 17, 224); Put16(fat, 19, 2880); fat[21] = 0xF0; Put16(fat, 22, 9);
        fat[510] = 0x55; fat[511] = 0xAA;
        foreach (int offset in new[] { 512, 10 * 512 })
        { fat[offset] = 0xF0; fat[offset + 1] = fat[offset + 2] = 0xFF; }
        return fat;
    }

    private static void NormalizeDates(byte[] bytes)
    {
        foreach (int sector in new[] { 16, 18 })
        { bytes.AsSpan(sector * SectorBytes + 174, 7).Clear(); bytes.AsSpan(sector * SectorBytes + 813, 34).Clear(); }
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    private static void Put32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), checked((uint)value));
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("UEFI boot: " + message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class ForwardOnlyStream(Stream output) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => output.Write(buffer);
        public override void Flush() => output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
