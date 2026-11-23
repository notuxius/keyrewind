using System;
using System.Collections.Generic;
using System.Globalization;

internal sealed class SnippetBuffer
{
    private string word = "";
    private int spaces;
    private uint spaceTime;
    private bool tooLong;

    internal void Clear()
    {
        word = "";
        spaces = 0;
        tooLong = false;
    }

    internal void Type(string text)
    {
        if (spaces > 0) Clear();
        if (tooLong) return;
        if (word.Length + text.Length > 64)
        {
            word = "";
            tooLong = true;
        }
        else word += text;
    }

    internal void Backspace()
    {
        if (spaces > 0) spaces--;
        else if (word.Length > 0)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(word);
            word = word.Substring(0, starts[starts.Length - 1]);
        }
    }

    internal bool Space(uint time, bool repeat, Dictionary<string, string> snippets, out string text, out int erase)
    {
        if (!repeat && spaces == 1 && unchecked(time - spaceTime) <= 650 && Find(snippets, 1, out text, out erase))
            return true;
        if (spaces < 2) spaces++;
        spaceTime = time;
        text = null;
        erase = 0;
        return false;
    }

    internal bool Find(Dictionary<string, string> snippets, int trailingSpaces, out string text, out int erase)
    {
        erase = 0;
        text = null;
        if (spaces != trailingSpaces || word.Length == 0 || !snippets.TryGetValue(word, out text)) return false;
        erase = StringInfo.ParseCombiningCharacters(word).Length + trailingSpaces;
        Clear();
        return true;
    }
}
