using System.Runtime.InteropServices;
using LibXblContainer;
using Spectre.Console;

var xboxSavesPath = Path.Join(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Packages",
    "BethesdaSoftworks.ProjectGold_3275kfvn8vcwc",
    "SystemAppData",
    "wgs"
    );

var availableContainers = Directory.Exists(xboxSavesPath) ? Directory.EnumerateDirectories(xboxSavesPath, "*_0000000000000000000000007BF72399", SearchOption.TopDirectoryOnly).ToList() : new List<string>();

ConnectedStorage storage = null!;

var xboxSaves = new List<string>();
if (availableContainers.Count == 0)
{
    AnsiConsole.MarkupLine(
        "[red]ERROR:[/] No Xbox save profile detected. Please start the game at least once.");
    AnsiConsole.Prompt(new ConfirmationPrompt("Press any key to exit."));
    return;
}

if (availableContainers.Count > 0)
{
    if (availableContainers.Count > 1)
    {
        AnsiConsole.MarkupLine(
            "[orange1]WARNING:[/] Multiple Xbox save profiles have been detected. Only the first one will be used.");
    }

    storage = new ConnectedStorage(availableContainers.First());

    xboxSaves = storage.Containers
        .Where(x => x.MetaData.FileName.StartsWith("Saves/"))
        .Select(container => container.MetaData.FileName["Saves/".Length..])
        .ToList();
}

var steamSavePath =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Starfield");

var steamSaves = new List<string>();
if (Path.Exists(steamSavePath))
{
    steamSaves = Directory.EnumerateFiles(steamSavePath, "*.sfs", SearchOption.TopDirectoryOnly)
        .Select(x => Path.GetFileName(x)!)
        .ToList();
}

var selectedSave = AnsiConsole.Prompt(new SelectionPrompt<string>()
    .Title("[white bold]Which save do you want to convert?[/]")
    .PageSize(16)
    .AddChoiceGroup("[white]Xbox[/]", xboxSaves.Select(x => $"[green]{x}[/]"))
    .AddChoiceGroup("[white]Steam[/]", steamSaves.Select(x => $"[yellow]{x}[/]")));

var isXboxSave = selectedSave.StartsWith("[green]");
var path = selectedSave[(selectedSave.IndexOf(']') + 1)..(selectedSave.LastIndexOf('['))];

var tempDir = Directory.CreateTempSubdirectory("ssc").FullName;
if (!isXboxSave)
{
    var savePath = Path.Join(steamSavePath, path);
    var save = StarfieldSave.LoadFromFile(savePath);

    save.SaveToDirectory(tempDir);

    using (storage)
    {
        var containerName = $"Saves/{path}";
        var container = storage.Get(containerName) ?? storage.Add(containerName, containerName);
        foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var data = File.ReadAllBytes(file);
            if (container.Blobs.Get(name) != null)
                container.Update(data, name);
            else
                container.Add(name, data);
        }
    }
}
else
{
    using (storage)
    {
        var containerName = $"Saves/{path}";
        var container = storage.Get(containerName)!;
        foreach (var blob in container.Blobs.Records)
        {
            var blobPath = Path.Combine(tempDir, blob.Name);
            using var fs = File.OpenWrite(blobPath);
            using var blobFs = container.Open(blob.Name);
            blobFs.CopyTo(fs);
        }
    }

    var save = StarfieldSave.LoadFromDirectory(tempDir);
    save.SaveToFile(Path.Join(steamSavePath, path));
}

AnsiConsole.MarkupLine("[green bold]Converted save successfully.[/]");

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct SaveHeader
{
    public const int Size = 0x48;

    public uint Magic;
    public uint Version;
    public ulong PartTableOffset;
    public ulong Unknown;
    public long PartTableEnd;
    public ulong TotalSaveSize;
    public ulong Unknown2;
    public ulong PartSize;
    public ulong Unknown4;
    public uint Flags;
    public uint SaveCompressionType;
}

public static class Extensions
{
    public static long Pad(this long value)
        => (value & 0xf) == 0 ? value : value + (0x10 - (value & 0xf));

    public static int Pad(this int value)
        => (value & 0xf) == 0 ? value : value + (0x10 - (value & 0xf));
}

class StarfieldSave
{
    private const string HeaderFilename = "BETHESDAPFH";

    private SaveHeader Header;

    private List<int> PartLengths;
    private List<byte[]> Parts;

    public StarfieldSave(BinaryReader reader, int headerLength = 0)
    {
        Header = MemoryMarshal.Cast<byte, SaveHeader>(reader.ReadBytes(SaveHeader.Size).AsSpan())[0];
        if (Header.Magic != 0x53504342) // BCPS
            throw new InvalidOperationException("Invalid magic");

        if (Header.Version != 1)
            throw new InvalidOperationException("Invalid version");

        if (Header.PartTableOffset != 0x48)
            throw new InvalidOperationException("Invalid part table offset");

        PartLengths = new List<int>();

        for (int i = 0; i < Math.Ceiling(Header.TotalSaveSize / (double)Header.PartSize); i++)
        {
            var len = reader.ReadInt32();
            PartLengths.Add(len);
        }

        if (reader.BaseStream.Position != Header.PartTableEnd && headerLength == 0)
        {
            reader.ReadBytes((int)(Header.PartTableEnd - reader.BaseStream.Position));
        }

        Parts = new List<byte[]>();
        foreach (var partLength in PartLengths)
        {
            Parts.Add(reader.ReadBytes(partLength));

            var padding = partLength.Pad() - partLength;
            if (padding != 0)
                reader.ReadBytes(padding);
        }

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            Console.Write("didnt read entire file sadge");
        }
    }

    public static StarfieldSave LoadFromFile(string path)
    {
        var file = File.ReadAllBytes(path);
        using var reader = new BinaryReader(new MemoryStream(file));

        return new StarfieldSave(reader);
    }

    public static StarfieldSave LoadFromDirectory(string path)
    {
        using var ms = new MemoryStream();
        var headerPath = Path.Join(path, HeaderFilename);

        var headerBytes = File.ReadAllBytes(headerPath);
        ms.Write(headerBytes);

        var parts = Directory.EnumerateFiles(path, "P*P", SearchOption.TopDirectoryOnly)
            .OrderBy(x => int.Parse(Path.GetFileNameWithoutExtension(x)[1..^1])).ToList();

        foreach (var part in parts)
        {
            ms.Write(File.ReadAllBytes(part));
        }

        ms.Seek(0, SeekOrigin.Begin);

        using var reader = new BinaryReader(ms);
        return new StarfieldSave(reader, headerBytes.Length);
    }

    public void SaveToFile(string path)
    {
        var paddingSpan = "padding\0padding\0"u8;

        using var fs = File.OpenWrite(path);

        Header.PartTableEnd = 0x48 + Parts.Count * 4;
        var padding = (int)(Header.PartTableEnd.Pad() - Header.PartTableEnd);
        var paddingOffset = (int)(Header.PartTableEnd & 0xf);
        Header.PartTableEnd += padding;

        fs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<SaveHeader>(Header)));

        foreach (var part in Parts)
            fs.Write(BitConverter.GetBytes(part.Length));

        if (padding != 0)
        {
            fs.Write(paddingSpan.Slice(paddingOffset, padding));
        }

        foreach (var part in Parts)
        {
            var partPadding = part.Length.Pad() - part.Length;
            var partPaddingOffset = part.Length & 0xf;

            fs.Write(part);

            if (partPadding != 0)
                fs.Write(paddingSpan.Slice(partPaddingOffset, partPadding));
        }
    }

    public void SaveToDirectory(string path)
    {
        Directory.CreateDirectory(path);

        var headerPath = Path.Join(path, HeaderFilename);
        using var headerFs = File.OpenWrite(headerPath);
        
        // Align
        Header.PartTableEnd = (0x48 + Parts.Count * 4).Pad();

        headerFs.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<SaveHeader>(Header)));
        foreach (var part in Parts)
            headerFs.Write(BitConverter.GetBytes(part.Length));

        for (int i = 0; i < Parts.Count; i++)
        {
            var partPath = Path.Join(path, $"P{i}P");
            using var fs = File.OpenWrite(partPath);
            fs.Write(Parts[i]);
        }
    }
}
