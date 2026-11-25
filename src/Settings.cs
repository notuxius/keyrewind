using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

[DataContract]
internal sealed class Hotkey
{
    [DataMember] public int Key;
    [DataMember] public int Modifiers;

    public bool Matches(int key, int modifiers) { return Key == key && Modifiers == modifiers; }

    public bool Valid()
    {
        return Key >= 1 && Key <= 254 && !WindowsInput.ModifierKey(Key) &&
            (Modifiers & ~15) == 0 && (Modifiers != 0 || Key >= (int)Keys.F1 && Key <= (int)Keys.F24);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & WindowsInput.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & WindowsInput.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & WindowsInput.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & WindowsInput.Win) != 0) parts.Add("Win");
        Keys key = (Keys)Key;
        string name = key >= Keys.D0 && key <= Keys.D9 ? ((char)Key).ToString() : key.ToString();
        parts.Add(name);
        return string.Join(" + ", parts);
    }
}

[DataContract]
internal sealed class Snippet
{
    [DataMember] public string Trigger;
    [DataMember] public string Text;
}

[DataContract]
internal sealed class Settings
{
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Path = System.IO.Path.Combine(LocalData, Program.Name, "settings.json");
    private static readonly string LegacyPath = System.IO.Path.Combine(LocalData, "LayoutAndSnippets", "settings.json");
    internal static string ReadPath { get { return File.Exists(Path) ? Path : LegacyPath; } }
    private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(Settings));

    [DataMember] public Hotkey Convert = CreateDefaultConvert();
    [DataMember] public Hotkey Expand;
    [DataMember] public int LayoutA;
    [DataMember] public int LayoutB;
    [DataMember] public bool ShowAutoswitchPopups = true;
    [DataMember(EmitDefaultValue = false)] public AppearanceMode Appearance;
    [DataMember(EmitDefaultValue = false)] public bool GamingMode;
    [DataMember(EmitDefaultValue = false)] public bool LaunchInBackground;
    [DataMember(EmitDefaultValue = false)] public bool ExitOnClose;
    [DataMember(EmitDefaultValue = false)] public bool CloseToTrayNoticeShown;
    [DataMember(EmitDefaultValue = false)] public Rectangle SettingsWindowBounds;
    [DataMember(EmitDefaultValue = false)] public bool SettingsWindowMaximized;
    [DataMember] public List<Snippet> Snippets = new List<Snippet>();
    internal Dictionary<string, string> Expansions;

    internal static Hotkey CreateDefaultConvert()
    {
        return new Hotkey { Key = (int)Keys.Z, Modifiers = WindowsInput.Shift | WindowsInput.Alt };
    }

    [OnDeserializing]
    private void SetDefaults(StreamingContext context) { ShowAutoswitchPopups = true; }

    internal static Settings Load()
    {
        Settings settings;
        string path = ReadPath;
        if (File.Exists(path))
        {
            using (var stream = File.OpenRead(path)) settings = (Settings)Serializer.ReadObject(stream);
        }
        else settings = new Settings();
        if (settings == null) throw new InvalidDataException("The settings file is empty.");
        if (!Enum.IsDefined(typeof(AppearanceMode), settings.Appearance)) settings.Appearance = AppearanceMode.System;
        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (Convert == null || !Convert.Valid() || Expand != null && !Expand.Valid())
            throw new InvalidDataException("Use a shortcut with Ctrl, Shift, Alt or Win, or choose a function key.");
        if (Expand != null && Convert.Matches(Expand.Key, Expand.Modifiers))
            throw new InvalidDataException("Choose different shortcuts for conversion and snippets.");
        if (LayoutA != 0 && LayoutA == LayoutB)
            throw new InvalidDataException("Choose two different keyboard layouts.");
        if (Snippets == null) throw new InvalidDataException("The settings file is missing its snippets list.");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Snippet snippet in Snippets)
        {
            if (snippet == null || string.IsNullOrEmpty(snippet.Trigger) || snippet.Trigger.Length > 64 ||
                snippet.Trigger.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                throw new InvalidDataException("Each abbreviation needs 1 to 64 characters without spaces.");
            if (string.IsNullOrEmpty(snippet.Text) || snippet.Text.Length > 4096 ||
                snippet.Text.Any(c => char.IsControl(c) || c == '\u2028' || c == '\u2029'))
                throw new InvalidDataException("Each replacement needs one line of 1 to 4096 characters.");
            if (entries.ContainsKey(snippet.Trigger))
                throw new InvalidDataException("An abbreviation can only be used once.");
            entries.Add(snippet.Trigger, snippet.Text);
        }
        Expansions = entries;
    }

    internal void Save()
    {
        Validate();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        string temporary = Path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) Serializer.WriteObject(stream, this);
            if (File.Exists(Path)) File.Replace(temporary, Path, null);
            else File.Move(temporary, Path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
