using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stepwright.Model;

namespace Stepwright.Export;

/// <summary>
/// A guide on disk is a single file: a zip holding guide.json plus the screenshots.
/// While it is open the app works from a folder under the local application data area.
/// </summary>
public static class GuideStore
{
    public const string FileExtension = ".stepwright";
    public const string FileFilter = "Stepwright guide (*.stepwright)|*.stepwright";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string WorkRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stepwright",
            "working");

    /// <summary>Creates an empty working folder for a fresh recording.</summary>
    public static string CreateWorkFolder()
    {
        string folder = Path.Combine(WorkRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Save(Guide guide, string path)
    {
        guide.Updated = DateTime.Now;
        PruneUnusedImages(guide);

        string temporary = path + ".tmp";
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("guide.json", CompressionLevel.Optimal);
            using (Stream writer = entry.Open())
            {
                JsonSerializer.Serialize(writer, guide, JsonOptions);
            }

            // Duplicated steps share one screenshot, so the same name must not be added twice.
            foreach (Step step in guide.Steps.Where(s => s.HasImage).DistinctBy(s => s.Image, StringComparer.OrdinalIgnoreCase))
            {
                string source = guide.ImagePath(step);
                if (!File.Exists(source))
                {
                    continue;
                }

                // Screenshots are already compressed, so storing them again would only cost time.
                ZipArchiveEntry media = archive.CreateEntry("media/" + step.Image, CompressionLevel.NoCompression);
                using Stream target = media.Open();
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.CopyTo(target);
            }
        }

        if (File.Exists(path))
        {
            // Replace swaps the two files in one step. If anything goes wrong the original
            // is still there, which matters because this may be the only copy of the guide.
            File.Replace(temporary, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }

        guide.FilePath = path;
        guide.Dirty = false;
    }

    public static Guide Load(string path)
    {
        string folder = CreateWorkFolder();
        string media = Path.Combine(folder, "media");
        Directory.CreateDirectory(media);

        Guide? guide = null;

        // A guide file can come from someone else, so the extract is bounded.
        const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
        const int MaxEntries = 5000;
        long written = 0;
        int count = 0;

        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (++count > MaxEntries || written > MaxExtractedBytes)
                {
                    throw new InvalidDataException("That guide file is larger than Stepwright will open.");
                }

                written += entry.Length;

                if (entry.FullName.Equals("guide.json", StringComparison.OrdinalIgnoreCase))
                {
                    using Stream reader = entry.Open();
                    guide = JsonSerializer.Deserialize<Guide>(reader, JsonOptions);
                }
                else if (entry.FullName.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(entry.Name))
                {
                    entry.ExtractToFile(Path.Combine(media, entry.Name), overwrite: true);
                }
            }
        }

        if (guide is null)
        {
            throw new InvalidDataException("That file does not contain a guide.");
        }

        guide.MediaFolder = media;
        guide.FilePath = path;
        guide.Dirty = false;
        return guide;
    }

    private static void PruneUnusedImages(Guide guide)
    {
        if (string.IsNullOrEmpty(guide.MediaFolder) || !Directory.Exists(guide.MediaFolder))
        {
            return;
        }

        var used = guide.Steps
            .Where(s => s.HasImage)
            .Select(s => s.Image)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.GetFiles(guide.MediaFolder, "*.png"))
        {
            if (!used.Contains(Path.GetFileName(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // The editor may still hold a preview. It will be cleaned up next time.
                }
            }
        }
    }

    /// <summary>Removes working folders left behind by earlier sessions.</summary>
    public static void CleanupOldWorkFolders(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(WorkRoot))
            {
                return;
            }

            DateTime cutoff = DateTime.Now - olderThan;
            foreach (string folder in Directory.GetDirectories(WorkRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTime(folder) < cutoff)
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch
                {
                    // A folder in use is simply skipped.
                }
            }
        }
        catch
        {
            // Housekeeping never blocks startup.
        }
    }

    public static string SuggestFileName(Guide guide)
    {
        string name = string.IsNullOrWhiteSpace(guide.Title) ? "guide" : guide.Title;
        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, ' ');
        }

        name = name.Trim();
        if (name.Length > 60)
        {
            name = name[..60].Trim();
        }

        return name.Length == 0 ? "guide" : name;
    }
}
