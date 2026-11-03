using System.IO;
using System.Text.Json.Nodes;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class EfiBootProjectTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "efi-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string family in new[] { ProjectDocument.CdFamily, ProjectDocument.DvdFamily })
            {
                string efi = Path.Combine(directory, "efi.img");
                ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "UEFI", family,
                    new ProjectGeometry(24, 58, 2048), [],
                    new ProjectOutputs(Path.Combine(directory, "output.iso"), false, string.Empty,
                        "BOOT", false, EfiBootImagePath: efi),
                    null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                string path = Path.Combine(directory, "boot.discproj");
                ProjectJsonStore.Save(path, document);
                ProjectDocument loaded = ProjectJsonStore.Load(path);
                Require(loaded.Outputs.EfiBootImagePath == efi, "boot image path round-trips");
                DiscParametersState state = new();
                ProjectSession.ApplyToState(loaded, state);
                Require(state.EfiBootImagePath == efi, "GUI restores boot setting");
                Require(ProjectSession.CaptureOutputs(loaded, state).EfiBootImagePath == efi,
                    "GUI captures boot setting without a data directory");
                if (family == ProjectDocument.CdFamily)
                    Require(!state.IsCdAudioMode && !state.IsCdWaveOutput && state.HasCdFiles,
                        "boot-only CD uses data mode");

                JsonObject legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                legacy["outputs"]!.AsObject().Remove("efiBootImagePath");
                File.WriteAllText(path, legacy.ToJsonString());
                ProjectDocument old = ProjectJsonStore.Load(path);
                ProjectSession.ApplyToState(old, state);
                Require(state.EfiBootImagePath == string.Empty, "legacy project clears previous boot setting");
                ProjectJsonStore.Save(path, old);
                Require(!File.ReadAllText(path).Contains("efiBootImagePath", StringComparison.Ordinal),
                    "disabled boot adds no field to legacy projects");
            }
            DiscParametersState cd = new() { CdOutputPath = Path.Combine(directory, "audio.wav") };
            cd.EfiBootImagePath = "efi.img";
            Require(cd.HasCdFiles && cd.CdOutputPath.EndsWith(".iso", StringComparison.Ordinal),
                "selecting boot image updates output format");
            cd.EfiBootImagePath = string.Empty;
            Require(cd.IsCdAudioMode && cd.IsCdWaveOutput, "disabling boot restores CD audio mode");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("efi-project: passed (persistence, GUI state, boot-only CD mode, legacy defaults)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("EFI project: " + message);
    }
}
