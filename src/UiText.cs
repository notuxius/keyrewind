using System;
using System.Collections.Generic;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

internal static class UiText
{
    // The language order, codes, and native names match Relf.
    internal static readonly KeyValuePair<string, string>[] Languages =
    {
        new KeyValuePair<string, string>("ar", "العربية"),
        new KeyValuePair<string, string>("bn", "বাংলা"),
        new KeyValuePair<string, string>("zh", "简体中文"),
        new KeyValuePair<string, string>("arz", "العربية المصرية"),
        new KeyValuePair<string, string>("en", "English"),
        new KeyValuePair<string, string>("fr", "Français"),
        new KeyValuePair<string, string>("de", "Deutsch"),
        new KeyValuePair<string, string>("hi", "हिन्दी"),
        new KeyValuePair<string, string>("id", "Bahasa Indonesia"),
        new KeyValuePair<string, string>("ja", "日本語"),
        new KeyValuePair<string, string>("pcm", "Naijíriá Píjin"),
        new KeyValuePair<string, string>("pt", "Português (Brasil)"),
        new KeyValuePair<string, string>("ru", "Русский"),
        new KeyValuePair<string, string>("es", "Español"),
        new KeyValuePair<string, string>("uk", "Українська"),
        new KeyValuePair<string, string>("ur", "اردو")
    };

    private static readonly Dictionary<string, string> English = new Dictionary<string, string>();
    private static Dictionary<string, string> messages = English;

    private static string language = "en";
    internal static string Language { get { return language; } }

    internal static RightToLeft Direction
    {
        get { return Language == "ar" || Language == "arz" || Language == "ur" ? RightToLeft.Yes : RightToLeft.No; }
    }

    internal static string Normalize(string code)
    {
        return Array.Exists(Languages, item => item.Key == code) ? code : "en";
    }

    internal static void UseLanguage(string code)
    {
        code = Normalize(code);
        if (code == Language) return;
        messages = Catalog(code);
        language = code;
    }

    internal static Dictionary<string, string> Catalog(string code)
    {
        if (code == "en") return English;
        using (var stream = typeof(UiText).Assembly.GetManifestResourceStream("locales." + code + ".json"))
        {
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            return (Dictionary<string, string>)serializer.ReadObject(stream);
        }
    }

    internal static string Get(string text)
    {
        string translation;
        return messages.TryGetValue(text, out translation) ? translation : text;
    }

    internal static string Get(string text, params object[] values)
    {
        return string.Format(Get(text), values);
    }
}
