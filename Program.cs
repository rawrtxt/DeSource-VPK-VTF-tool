using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace DeSource; // jack in kick ass

public record VpkEntry(
    string Extension,
    string Folder,
    string Filename,
    uint Crc,
    ushort PreloadBytes,
    ushort ArchiveIndex,
    uint EntryOffset,
    uint EntryLength,
    byte[] PreloadData
)
{
    public string FullPath
    {
        get
        {
            var cleanFolder = Folder.Trim(' ', '/', '\\');
            return string.IsNullOrEmpty(cleanFolder)
                ? $"{Filename}.{Extension}"
                : $"{cleanFolder}/{Filename}.{Extension}";
        }
    }
}

public static class Program
{
    public const uint VPK_SIGNATURE = 0x55AA1234;
    public const ushort VPK_DIR_ARCHIVE_INDEX = 0x7FFF; // i dont understand why this is 0x7FFF but it is, so whatever
    public static readonly byte[] VERPACKED_MAGIC = "VERP"u8.ToArray();

    public const string AES_KEY_HEX = "9e5f0871ed1cfa534c75623d5a25efa3ffbff5a2fceea1bbbb60f6a603ab188e"; // this is the AES-256 key if you see this FUCK YOU reverse engineer im having a bad day and i dont want to deal with your shit right now
    public static readonly byte[] EncryptionKey = Convert.FromHexString(AES_KEY_HEX);

    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? initialVpk = null;
        if (args.Length > 0 && File.Exists(args[0]) && args[0].EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            initialVpk = Path.GetFullPath(args[0]);
        }

        Application.Run(new MainForm(initialVpk));
    }

    public static void SafeRead(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = RandomAccess.Read(handle, buffer.Slice(totalRead), offset + totalRead);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of file while reading VPK archive.");
            totalRead += read;
        }
    }
}

public class MainForm : Form
{
    private Panel _dropPanel = null!;
    private Label _lblDropText = null!;
    private TextBox _txtOutput = null!;
    private Button _btnBrowseOutput = null!;
    private CheckBox _chkConvertPng = null!;
    private CheckBox _chkKeepVtf = null!;
    private CheckBox _chkKeepVmt = null!;
    private Button _btnStart = null!;
    private ProgressBar _progressBar = null!;
    private RichTextBox _txtLog = null!;

    private string? _selectedVpkPath;

    public MainForm(string? initialVpk = null)
    {
        InitializeComponents();
        if (!string.IsNullOrEmpty(initialVpk))
        {
            SetVpkFile(initialVpk);
        }
    }

    private void InitializeComponents()
    {
        Text = "deSource — VPK Decompiler & VTF to PNG";
        Size = new Size(680, 640);
        MinimumSize = new Size(600, 550);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // Drag & Drop Area
        _dropPanel = new Panel
        {
            Location = new Point(15, 15),
            Size = new Size(635, 100),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(245, 247, 250),
            AllowDrop = true
        };

        _lblDropText = new Label
        {
            Text = "Drag and drop a .vpk file here\n(or click to browse)",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold)
        };

        _dropPanel.Controls.Add(_lblDropText);
        _dropPanel.Click += (s, e) => BrowseVpk();
        _lblDropText.Click += (s, e) => BrowseVpk();

        _dropPanel.DragEnter += (s, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };

        _dropPanel.DragDrop += (s, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (files[0].EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    SetVpkFile(files[0]);
                else
                    MessageBox.Show(this, "Only files with .vpk extension are supported.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        // Settings Panel
        var grpSettings = new GroupBox
        {
            Text = "Decompiler Settings",
            Location = new Point(15, 125),
            Size = new Size(635, 130),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var lblOut = new Label { Text = "Output Path:", Location = new Point(15, 25), AutoSize = true };
        _txtOutput = new TextBox { Location = new Point(100, 22), Size = new Size(420, 23), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _btnBrowseOutput = new Button { Text = "Browse...", Location = new Point(530, 21), Size = new Size(90, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnBrowseOutput.Click += (s, e) => BrowseOutput();

        _chkConvertPng = new CheckBox
        {
            Text = "Convert VTF textures to PNG",
            Location = new Point(15, 60),
            AutoSize = true,
            Checked = true
        };

        _chkKeepVtf = new CheckBox
        {
            Text = "Keep original .vtf files",
            Location = new Point(15, 90),
            AutoSize = true,
            Checked = false
        };

        _chkKeepVmt = new CheckBox
        {
            Text = "Keep .vmt (material definitions)",
            Location = new Point(250, 90),
            AutoSize = true,
            Checked = false
        };

        grpSettings.Controls.AddRange(new Control[] { lblOut, _txtOutput, _btnBrowseOutput, _chkConvertPng, _chkKeepVtf, _chkKeepVmt });

        // Action Button
        _btnStart = new Button
        {
            Text = "Decompile",
            Location = new Point(15, 265),
            Size = new Size(635, 38),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnStart.Click += async (s, e) => await StartDecompileAsync();

        // Progress Bar & Logs
        _progressBar = new ProgressBar // im push progress
        {
            Location = new Point(15, 312),
            Size = new Size(635, 15),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _txtLog = new RichTextBox
        {
            Location = new Point(15, 335),
            Size = new Size(635, 245),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.LightGray,
            Font = new Font("Consolas", 9F)
        };

        Controls.AddRange(new Control[] {
            _dropPanel, grpSettings, _btnStart, _progressBar, _txtLog
        });
    }

    private void SetVpkFile(string path)
    {
        _selectedVpkPath = path;
        _lblDropText.Text = $"Selected Archive:\n{Path.GetFileName(path)}";
        _lblDropText.ForeColor = Color.DarkSlateBlue;

        string outName = Path.GetFileNameWithoutExtension(path);
        if (outName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            outName = outName[..^4];

        _txtOutput.Text = Path.Combine(Path.GetDirectoryName(path)!, outName);
        Log($"Selected archive: {path}");
    }

    private void BrowseVpk()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "Source VPK Archives (*.vpk)|*.vpk|All Files (*.*)|*.*",
            Title = "Select VPK Archive"
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            SetVpkFile(ofd.FileName);
    }

    private void BrowseOutput() // im hate microsoft, im like Linus Torvalds, i hate microsoft, i hate windows, i hate .NET, i hate C#, i hate Visual Studio, i hate all of this shit. but im using it anyway because im a masochist and i like pain. and also because im too lazy to write my own GUI framework. so here we are.
    {
        using var fbd = new FolderBrowserDialog { Description = "Select Destination Directory" };
        if (fbd.ShowDialog(this) == DialogResult.OK)
            _txtOutput.Text = fbd.SelectedPath;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            Invoke(() => Log(message));
            return;
        }
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _txtLog.ScrollToCaret();
    }

    private async Task StartDecompileAsync()
    {
        if (string.IsNullOrEmpty(_selectedVpkPath) || !File.Exists(_selectedVpkPath))
        {
            MessageBox.Show(this, "Please drag & drop or select a VPK file first.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string outDir = _txtOutput.Text.Trim();
        if (string.IsNullOrEmpty(outDir))
        {
            MessageBox.Show(this, "Please specify an output directory.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool convertPng = _chkConvertPng.Checked;
        bool keepVtf = _chkKeepVtf.Checked;
        bool keepVmt = _chkKeepVmt.Checked;

        _btnStart.Enabled = false;
        _progressBar.Style = ProgressBarStyle.Marquee;

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(outDir);
                ExtractVpk(_selectedVpkPath, outDir);

                if (convertPng)
                {
                    string vtfCmd = EnsureToolsExtracted();
                    ConvertVtfToPng(vtfCmd, outDir, keepVtf);
                }

                if (!keepVmt)
                {
                    CleanupVmts(outDir);
                }
            });

            Log(">>> All operations completed successfully!");
            MessageBox.Show(this, "Decompilation and asset conversion completed successfully!", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"[ERROR]: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Decompilation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 0;
            _btnStart.Enabled = true;
        }
    }
// cyberpunk 2077 is a good game, stop what? why in vpk decompile tool im talking about cyberpunk 2077? because i want to, shut up. anyway, this is the main function that extracts the vpk file and its contents. it reads the vpk header, parses the directory tree, and extracts each file in parallel. it also handles preload data and archive chunks. if you want to understand how vpk files work, read the code and comments carefully.
    private void ExtractVpk(string vpkPath, string outputDir)
    {
        Log($"Parsing directory tree: {Path.GetFileName(vpkPath)}...");

        using var fs = File.OpenRead(vpkPath);
        using var reader = new BinaryReader(fs, Encoding.UTF8);

        uint sig = reader.ReadUInt32();
        if (sig != Program.VPK_SIGNATURE)
            throw new InvalidDataException($"Invalid VPK signature: 0x{sig:X8}");

        uint version = reader.ReadUInt32();
        uint treeSize = reader.ReadUInt32();

        long headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new NotSupportedException($"VPK version v{version} is not supported.")
        };

        if (version == 2)
            fs.Seek(headerSize, SeekOrigin.Begin);

        var entries = new List<VpkEntry>();

        while (true)
        {
            string ext = ReadNullString(reader);
            if (string.IsNullOrEmpty(ext)) break;

            while (true)
            {
                string folder = ReadNullString(reader);
                if (string.IsNullOrEmpty(folder)) break;

                while (true)
                {
                    string filename = ReadNullString(reader);
                    if (string.IsNullOrEmpty(filename)) break;

                    uint crc = reader.ReadUInt32();
                    ushort preloadBytes = reader.ReadUInt16();
                    ushort archiveIdx = reader.ReadUInt16();
                    uint offset = reader.ReadUInt32();
                    uint length = reader.ReadUInt32();
                    ushort terminator = reader.ReadUInt16();

                    byte[] preloadData = preloadBytes > 0 ? reader.ReadBytes(preloadBytes) : Array.Empty<byte>();

                    entries.Add(new VpkEntry(
                        ext, folder, filename, crc, preloadBytes,
                        archiveIdx, offset, length, preloadData
                    ));
                }
            }
        }

        Log($"Found {entries.Count} entries. Extracting files in parallel...");

        var handleCache = new ConcurrentDictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Parallel.ForEach(entries, entry =>
            {
                string targetPath = Path.Combine(outputDir, entry.FullPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var outFs = File.Create(targetPath);

                if (entry.PreloadBytes > 0)
                    outFs.Write(entry.PreloadData);

                if (entry.EntryLength > 0)
                {
                    string chunkPath;
                    long actualOffset;

                    if (entry.ArchiveIndex == Program.VPK_DIR_ARCHIVE_INDEX)
                    {
                        chunkPath = vpkPath;
                        actualOffset = headerSize + treeSize + entry.EntryOffset;
                    }
                    else
                    {
                        string dirName = Path.GetFileName(vpkPath);
                        string chunkName;

                        if (dirName.EndsWith("_dir.vpk", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = dirName.LastIndexOf("_dir.vpk", StringComparison.OrdinalIgnoreCase);
                            chunkName = dirName[..idx] + $"_{entry.ArchiveIndex:D3}.vpk";
                        }
                        else
                        {
                            chunkName = $"{Path.GetFileNameWithoutExtension(vpkPath)}_{entry.ArchiveIndex:D3}.vpk";
                        }

                        chunkPath = Path.Combine(Path.GetDirectoryName(vpkPath)!, chunkName);
                        actualOffset = entry.EntryOffset;
                    }

                    SafeFileHandle handle = handleCache.GetOrAdd(chunkPath, path =>
                    {
                        if (!File.Exists(path))
                            throw new FileNotFoundException($"Missing VPK archive chunk: {Path.GetFileName(path)}");
                        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    });

                    byte[] buffer = new byte[entry.EntryLength];
                    Program.SafeRead(handle, buffer, actualOffset);
                    outFs.Write(buffer);
                }
            });
        }
        finally
        {
            foreach (var h in handleCache.Values)
                h.Dispose();
        }

        Log("VPK extraction finished.");
    }

    private string EnsureToolsExtracted()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string verpackedPath = Path.Combine(baseDir, "tools.verpacked");

        if (!File.Exists(verpackedPath))
        {
            string directVtfCmd = Path.Combine(baseDir, "VTFCmd.exe");
            if (File.Exists(directVtfCmd))
                return directVtfCmd;

            throw new FileNotFoundException($"File 'tools.verpacked' was not found in the application directory:\n{verpackedPath}");
        }

        string runtimeDir = Path.Combine(Path.GetTempPath(), "deSource_runtime_tools");
        string vtfCmdExe = Path.Combine(runtimeDir, "VTFCmd.exe");

        if (File.Exists(vtfCmdExe))
            return vtfCmdExe;

        Log("Decrypting VTFEdit tools from tools.verpacked...");
        Directory.CreateDirectory(runtimeDir);

        using var fs = File.OpenRead(verpackedPath);
        using var reader = new BinaryReader(fs);

        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Program.VERPACKED_MAGIC))
            throw new InvalidDataException("Invalid .verpacked file format.");

        byte[] nonce = reader.ReadBytes(12);
        byte[] tag = reader.ReadBytes(16);
        byte[] cipherBytes = reader.ReadBytes((int)(fs.Length - fs.Position));

        byte[] plainBytes = new byte[cipherBytes.Length];
        using (var aesGcm = new AesGcm(Program.EncryptionKey, 16))
        {
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }

        using var memZip = new MemoryStream(plainBytes);
        using var archive = new ZipArchive(memZip, ZipArchiveMode.Read);
        archive.ExtractToDirectory(runtimeDir, overwriteFiles: true);

        if (!File.Exists(vtfCmdExe))
            throw new FileNotFoundException("VTFCmd.exe was not found inside the decrypted tools package.");

        return vtfCmdExe;
    }

    private void ConvertVtfToPng(string vtfCmdPath, string outputDir, bool keepVtf)
    {
        Log("Running batch VTF -> PNG texture conversion...");

        var startInfo = new ProcessStartInfo
        {
            FileName = vtfCmdPath,
            Arguments = $"-folder \"{outputDir}\\*.vtf\" -output \"{outputDir}\" -exportformat \"png\" -recurse -silent",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(vtfCmdPath)!
        };

        using var process = Process.Start(startInfo);
        process?.WaitForExit();

        if (!keepVtf)
        {
            Log("Cleaning up temporary .vtf files...");
            int count = 0;
            foreach (var vtf in Directory.EnumerateFiles(outputDir, "*.vtf", SearchOption.AllDirectories))
            {
                try { File.Delete(vtf); count++; } catch { }
            }
            Log($"Deleted {count} original VTF files.");
        }
    }

    private void CleanupVmts(string outputDir)
    {
        Log("Cleaning up .vmt material files...");
        int count = 0;
        foreach (var vmt in Directory.EnumerateFiles(outputDir, "*.vmt", SearchOption.AllDirectories))
        {
            try { File.Delete(vmt); count++; } catch { }
        }
        Log($"Deleted {count} .vmt files.");
    }

    private static string ReadNullString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}

// this code written by a human, not an AI. if you see this and think "wow this is good code" then you are wrong, it is bad code and i am a bad programmer. i am sorry for the state of this codebase.
// its joke, this code written by totally BADASS programmer, and if you see this and think "wow this is good code" then you are right, it is good code and i am a good programmer. i am proud of the state of this codebase.